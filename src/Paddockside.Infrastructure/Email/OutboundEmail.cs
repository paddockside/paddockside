using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Email;

/// <summary>
/// Step one of the send pipeline, inside the compose request: one delivery per recipient the access rule admits
/// now, each with its own routing token (messaging-channels.md §1.2–§1.3).
/// </summary>
public static class OutboundQueue
{
    /// <param name="channel">
    /// Staff's override, or null for "Preferred": each owner by their own choices for the message's category
    /// (messaging-channels.md §6). An override still respects an owner who switched a non-important category off.
    /// </param>
    public static async Task<IReadOnlyList<Delivery>> QueueAsync(
        PaddocksideDbContext db, Tenant tenant, Horse horse, Event? @event, StreamItem message, DeliveryChannel? channel, bool smsAvailable, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var category = NotificationCategories.For(message, @event);
        var audience = AccessRule.CurrentOwnerAudience(tenant, horse, message).ToList();
        var parties = await db.Parties.Where(p => audience.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var choices = (await db.NotificationPreferences.Where(p => audience.Contains(p.PartyId) && p.Category == category.Code).ToListAsync(cancellationToken))
            .ToLookup(p => p.PartyId);

        IReadOnlySet<DeliveryChannel> ChannelsFor(Guid partyId)
        {
            if (!parties.TryGetValue(partyId, out var party)) return new HashSet<DeliveryChannel>();
            var preferred = NotificationCategories.ChannelsFor(category, choices[partyId], party.PrimaryEmail is not null, party.PrimaryMobile is not null, smsAvailable);
            if (channel is not { } forced) return preferred;
            var optedOut = !category.Important && NotificationCategories.Chosen(category, choices[partyId]).Count == 0;
            return optedOut ? new HashSet<DeliveryChannel>() : new HashSet<DeliveryChannel> { forced };
        }

        var deliveries = Delivery.ForOwners(tenant, horse, message, ChannelsFor, now);
        var issued = new HashSet<string>();
        // Email replies come back through a per-recipient address; a text reply is matched by the sender's number.
        foreach (var delivery in deliveries.Where(d => d.Channel == DeliveryChannel.Email))
        {
            var token = await NewUniqueTokenAsync(db, issued, cancellationToken);
            var address = RoutingAddress.ForRecipient(message, delivery.PartyId, token, now);
            delivery.AssignRoutingAddress(address);
            db.RoutingAddresses.Add(address);
        }

        db.Deliveries.AddRange(deliveries);
        return deliveries;
    }

    /// <summary>
    /// A staff reply to someone who wrote in: one email to them alone, with its own reply address so their answer
    /// threads back under it (messaging-channels.md §3.5).
    /// </summary>
    public static async Task<Delivery> QueueDirectAsync(PaddocksideDbContext db, StreamItem message, Party recipient, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var delivery = Delivery.ToSender(message, recipient, now);
        var address = RoutingAddress.ForRecipient(message, recipient.Id, await NewUniqueTokenAsync(db, new HashSet<string>(), cancellationToken), now);
        delivery.AssignRoutingAddress(address);
        db.RoutingAddresses.Add(address);
        db.Deliveries.Add(delivery);
        return delivery;
    }

    /// <summary>
    /// Tokens are unique across the product, so the check looks across every tenant — it asks only whether the
    /// token exists, never reads another tenant's row. The unique index is the final guarantee.
    /// </summary>
    internal static async Task<string> NewUniqueTokenAsync(PaddocksideDbContext db, ISet<string> issued, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var token = RoutingToken.New();
            if (issued.Contains(token)) continue;
            if (await db.RoutingAddresses.IgnoreQueryFilters().AnyAsync(r => r.Token == token, cancellationToken)) continue;
            issued.Add(token);
            return token;
        }

        throw new InvalidOperationException("Could not issue a unique routing token.");
    }
}

/// <summary>
/// Step two, in the background: sends queued email deliveries through the provider, tenant by tenant. Each
/// recipient is checked against the access rule again at the moment of sending, so an owner who exited after the
/// message was queued is not emailed (data-model.md DELIVERY).
/// </summary>
public sealed class EmailDispatcher(
    TenantScopedDb tenants,
    IEmailSender sender,
    OwnerEmailRenderer renderer,
    IMediaStore files,
    IOptions<EmailOptions> options,
    ClientAccess.ClientSignIn links,
    TimeProvider clock,
    ILogger<EmailDispatcher> logger)
{
    private const int BatchSize = 50;

    /// <summary>Sends what is queued; returns how many emails the provider accepted.</summary>
    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured) return 0;

        var accepted = 0;
        foreach (var tenantId in await tenants.TenantsWithQueuedEmailAsync(20, cancellationToken))
            accepted += await DispatchTenantAsync(tenantId, cancellationToken);
        if (InvitationsEnabled)
            foreach (var tenantId in await tenants.TenantsWithQueuedInvitationsAsync(20, cancellationToken))
                accepted += await DispatchInvitationsAsync(tenantId, cancellationToken);
        if (MailoutsEnabled)
            foreach (var tenantId in await tenants.TenantsWithQueuedMailoutsAsync(20, cancellationToken))
                accepted += await DispatchMailoutsAsync(tenantId, cancellationToken);
        return accepted;
    }

    /// <summary>Whether any email is still waiting to be sent (and has attempts left).</summary>
    public async Task<bool> HasQueuedAsync(CancellationToken cancellationToken) =>
        (await tenants.TenantsWithQueuedEmailAsync(1, cancellationToken)).Count > 0
        || (InvitationsEnabled && (await tenants.TenantsWithQueuedInvitationsAsync(1, cancellationToken)).Count > 0)
        || (MailoutsEnabled && (await tenants.TenantsWithQueuedMailoutsAsync(1, cancellationToken)).Count > 0);

    /// <summary>Invitations carry a portal link, so they wait until the portal address is configured.</summary>
    private bool InvitationsEnabled => options.Value.PortalBaseUrl is { Length: > 0 };

    /// <summary>Mailouts carry an unsubscribe link on the app's address, and go on the broadcast stream.</summary>
    private bool MailoutsEnabled => options.Value.PortalBaseUrl is { Length: > 0 } && sender.CanBroadcast;

    /// <summary>
    /// Sends queued mailouts, one email per recipient: their own reply address (m-{token}), so a reply comes back to
    /// the mailout, and their own unsubscribe link, in the footer and in the one-click header mail apps show
    /// (messaging-channels.md §2.1, §7). Someone who unsubscribed or whose address failed since it was queued is skipped.
    /// </summary>
    private async Task<int> DispatchMailoutsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var queued = await db.MailoutRecipients.Where(r => r.Status == MailoutRecipientStatus.Queued).OrderBy(r => r.StatusAt).Take(BatchSize).ToListAsync(cancellationToken);
        if (queued.Count == 0) return 0;

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var mailoutIds = queued.Select(r => r.MailoutId).Distinct().ToList();
        var mailouts = await db.Mailouts.Where(m => mailoutIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancellationToken);
        var partyIds = queued.Select(r => r.PartyId).ToList();
        var parties = await db.Parties.Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var attachments = await AttachmentsAsync(db, mailoutIds, cancellationToken);

        var accepted = 0;
        foreach (var recipient in queued)
        {
            var now = clock.GetUtcNow();
            var party = parties[recipient.PartyId];
            if (party.UnsubscribedAt is not null) { recipient.Skip("Unsubscribed before it was sent", now); continue; }
            if (party.PrimaryEmail is not { IsDeliverable: true } contact) { recipient.Skip("No working email address", now); continue; }
            if (attachments[recipient.MailoutId] is not { } files) { recipient.Skip("Not sent: one of its attachments couldn't be found", now); continue; }

            var unsubscribe = Unsubscribe.Link(options.Value.PortalBaseUrl!, tenant.Slug, recipient.Token);
            var replyTo = $"m-{recipient.Token}@{tenant.Slug}.{options.Value.InboundDomain}";
            var rendered = renderer.RenderMailout(tenant, mailouts[recipient.MailoutId], party, unsubscribe);
            var result = await sender.SendAsync(new OutboundEmail(options.Value.FromAddress, Senders.For(tenant.Name), contact.Value, party.DisplayName, replyTo,
                rendered.Subject, rendered.Html, rendered.Text,
                new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString(), ["mailoutRecipientId"] = recipient.Id.ToString() },
                TrackOpens: true, Broadcast: true,
                Headers: new Dictionary<string, string>
                {
                    ["List-Unsubscribe"] = $"<{unsubscribe}>",
                    ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                },
                Attachments: files), cancellationToken);

            if (result.Accepted) { recipient.MarkSent(contact.Value, result.ProviderMessageId!, now); accepted++; }
            else if (result.PermanentFailure) { recipient.Skip($"Not sent: {result.Error}", now); party.MarkUndeliverable(contact.Value, now); }
        }

        await db.SaveChangesAsync(cancellationToken);
        return accepted;
    }

    /// <summary>
    /// Each mailout's files, read once for the whole batch; null for a mailout with a file missing from storage, which is
    /// then not sent rather than sent without it.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<EmailAttachment>?>> AttachmentsAsync(PaddocksideDbContext db, List<Guid> mailoutIds, CancellationToken cancellationToken)
    {
        var rows = await db.MailoutAttachments.Where(a => a.MailoutId != null && mailoutIds.Contains(a.MailoutId.Value)).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        var result = mailoutIds.ToDictionary(id => id, _ => (IReadOnlyList<EmailAttachment>?)Array.Empty<EmailAttachment>());
        foreach (var group in rows.GroupBy(a => a.MailoutId!.Value))
        {
            var read = new List<EmailAttachment>();
            foreach (var a in group)
            {
                await using var stream = await files.OpenReadAsync(a.BlobName, cancellationToken);
                if (stream is null) { logger.LogError("Mailout {MailoutId} attachment {BlobName} is missing from storage.", group.Key, a.BlobName); read = null; break; }
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, cancellationToken);
                read.Add(new EmailAttachment(a.FileName, a.ContentType, copy.ToArray()));
            }

            result[group.Key] = read;
        }

        return result;
    }

    private async Task<int> DispatchTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);

        var batch = await db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Queued && d.Channel == DeliveryChannel.Email && d.Attempts < Delivery.MaxAttempts)
            .OrderBy(d => d.StatusAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0) return 0;

        var itemIds = batch.Select(d => d.StreamItemId).Distinct().ToList();
        var items = await db.StreamItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var horseIds = items.Values.Select(i => i.HorseId).Distinct().ToList();
        var horses = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, cancellationToken);
        var eventIds = items.Values.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
        var events = await db.Events.Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var partyIds = batch.Select(d => d.PartyId).Distinct().ToList();
        var parties = await db.Parties.Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var routingIds = batch.Where(d => d.RoutingAddressId != null).Select(d => d.RoutingAddressId!.Value).ToList();
        var routing = await db.RoutingAddresses.Where(r => routingIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);

        var accepted = 0;
        var issued = new HashSet<string>();
        foreach (var delivery in batch)
        {
            var now = clock.GetUtcNow();
            var message = items[delivery.StreamItemId];
            var horse = horses[message.HorseId];
            var party = parties[delivery.PartyId];

            // A direct reply goes to whoever wrote in, owner or not; an owner update only to current owners.
            if (!delivery.Direct && !AccessRule.CurrentOwnerAudience(tenant, horse, message).Contains(party.Id))
            {
                delivery.Suppress("Not sent: no longer an owner when the email went out.", now);
                continue;
            }

            if (party.PrimaryEmail is not { } contact)
            {
                delivery.Suppress("Not sent: no email address on record.", now);
                continue;
            }

            if (!contact.IsDeliverable)
            {
                delivery.Suppress("Not sent: this address bounced or complained before.", now, contact.Value);
                continue;
            }

            if (delivery.RoutingAddressId is not { } routingId || !routing.TryGetValue(routingId, out var address))
            {
                address = RoutingAddress.ForRecipient(message, party.Id, await OutboundQueue.NewUniqueTokenAsync(db, issued, cancellationToken), now);
                db.RoutingAddresses.Add(address);
                delivery.AssignRoutingAddress(address);
            }

            // Each owner's copy carries its own link: it opens this message and signs them in (identity-access.md §4.1).
            // A direct reply carries no portal link: the person who wrote in may not be an owner at all.
            var onEvent = message.EventId is { } eventId ? events.GetValueOrDefault(eventId) : null;
            RenderedEmail rendered;
            if (delivery.Direct)
            {
                rendered = renderer.RenderDirectReply(tenant, horse, onEvent, message, party);
            }
            else
            {
                var openLink = await links.IssueDeepLinkAsync(tenant.Id, party.Id, contact.Value, message.Id, message.EventId, cancellationToken);
                var choices = options.Value.PortalBaseUrl is { Length: > 0 } portal ? $"{portal.TrimEnd('/')}/my/me#notifications" : null;
                rendered = renderer.Render(tenant, horse, onEvent, message, party, openLink, choices);
            }
            var result = await sender.SendAsync(new OutboundEmail(
                options.Value.FromAddress,
                Senders.For(tenant.Name),
                contact.Value,
                party.DisplayName,
                address.EmailAddress(tenant, options.Value.InboundDomain),
                rendered.Subject,
                rendered.Html,
                rendered.Text,
                new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString(), ["deliveryId"] = delivery.Id.ToString() }), cancellationToken);

            if (result.Accepted)
            {
                delivery.MarkSent(contact.Value, result.ProviderMessageId!, now);
                accepted++;
            }
            else
            {
                delivery.RecordFailedAttempt(result.Error ?? "Not accepted", result.PermanentFailure, now, contact.Value);
                if (result.PermanentFailure) party.MarkUndeliverable(contact.Value, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Email dispatch for tenant {TenantId}: {Accepted} of {Count} accepted.", tenantId, accepted, batch.Count);
        return accepted;
    }

    /// <summary>
    /// Sends queued owner invitations (identity-access.md §6). A reply to one goes to the horse's own inbox, so it
    /// reaches staff on that horse rather than bouncing.
    /// </summary>
    private async Task<int> DispatchInvitationsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var queued = await db.OwnerInvitations.Where(i => i.Status == OwnerInvitationStatus.Queued).OrderBy(i => i.CreatedAt).Take(BatchSize).ToListAsync(cancellationToken);
        if (queued.Count == 0) return 0;

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var partyIds = queued.Select(i => i.PartyId).ToList();
        var parties = await db.Parties.Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var horseIds = queued.Select(i => i.HorseId).Distinct().ToList();
        var horses = await db.Horses.Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, cancellationToken);
        var inboxes = await db.RoutingAddresses.Where(r => r.Kind == RoutingAddressKind.HorseInbox && horseIds.Contains(r.HorseId)).ToListAsync(cancellationToken);

        var accepted = 0;
        foreach (var invitation in queued)
        {
            var now = clock.GetUtcNow();
            var party = parties[invitation.PartyId];
            var horse = horses[invitation.HorseId];
            if (party.PersonId is not null) { invitation.Skip("Not sent: they already sign in.", now); continue; }
            if (party.PrimaryEmail is not { } contact) { invitation.Skip("Not sent: no email address on record.", now); continue; }
            if (!contact.IsDeliverable) { invitation.Skip("Not sent: this address bounced or complained before.", now); continue; }
            if (await links.IssueInvitationAsync(tenant.Id, party.Id, contact.Value, cancellationToken) is not { } link) continue;

            var replyTo = Inbound.HorseInboxes.Current(horse, inboxes)?.EmailAddress(tenant, options.Value.InboundDomain) ?? options.Value.FromAddress;
            var rendered = renderer.RenderInvitation(tenant, horse, party, link);
            // No open-tracking pixel on anything carrying a sign-in link: a hidden tracker there reads as phishing.
            var result = await sender.SendAsync(new OutboundEmail(options.Value.FromAddress, Senders.For(tenant.Name), contact.Value, party.DisplayName, replyTo,
                rendered.Subject, rendered.Html, rendered.Text,
                new Dictionary<string, string> { ["tenantId"] = tenant.Id.ToString(), ["invitationId"] = invitation.Id.ToString() }, TrackOpens: false), cancellationToken);

            if (result.Accepted)
            {
                invitation.MarkSent(contact.Value, now);
                accepted++;
            }
            else if (result.PermanentFailure)
            {
                invitation.Skip($"Not sent: {result.Error}", now);
                party.MarkUndeliverable(contact.Value, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return accepted;
    }
}
