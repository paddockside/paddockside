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
    public static async Task<IReadOnlyList<Delivery>> QueueAsync(
        PaddocksideDbContext db, Tenant tenant, Horse horse, StreamItem message, DeliveryChannel channel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deliveries = Delivery.ForOwners(tenant, horse, message, channel, now);
        var issued = new HashSet<string>();
        foreach (var delivery in deliveries)
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
    IOptions<EmailOptions> options,
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
        return accepted;
    }

    /// <summary>Whether any email is still waiting to be sent (and has attempts left).</summary>
    public async Task<bool> HasQueuedAsync(CancellationToken cancellationToken) =>
        (await tenants.TenantsWithQueuedEmailAsync(1, cancellationToken)).Count > 0;

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

            if (!AccessRule.CurrentOwnerAudience(tenant, horse, message).Contains(party.Id))
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

            var rendered = renderer.Render(tenant, horse, message.EventId is { } eventId ? events.GetValueOrDefault(eventId) : null, message, party);
            var result = await sender.SendAsync(new OutboundEmail(
                options.Value.FromAddress,
                tenant.Name,
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
}
