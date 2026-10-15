using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>
/// Step two, off the queue (data-model.md §3, the pipeline): safety checks, then attribution by address.
/// <list type="bullet">
/// <item>Spam (the provider's verdict) → Held. Auto-replies and bounces → Ignored.</item>
/// <item>Tier 1: a reply token resolves sender, message, event and horse exactly; the reply is threaded under the
/// original message and the recipient's delivery is marked Replied.</item>
/// <item>Tier 2: a horse address resolves the horse; the single open event in its expected window gets it,
/// otherwise it is parked on the horse.</item>
/// <item>Tier 3: anything else (a general address) is scored against every managed horse by what it says and who
/// sent it. A clear winner is Suggested for staff to confirm in one click; otherwise Pending, with the best guesses
/// kept as candidates. Tier 3 never places mail by itself.</item>
/// </list>
/// All of it runs inside the tenant the mail was addressed to, so a token from another tenant simply is not found.
/// </summary>
public sealed partial class InboundProcessor(TenantScopedDb tenants, IOptions<EmailOptions> email, TimeProvider clock, IInboundStore inbound, IMediaStore media)
{
    public async Task ProcessAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var message = await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == inboundMessageId, cancellationToken);
        if (message is null || message.State != InboundState.Received) return; // processed already: the queue redelivered

        var now = clock.GetUtcNow();
        var headers = ReadHeaders(message.Headers);
        var display = QuotedText.Strip(message.TextBody ?? HtmlToText(message.HtmlBody));

        if (AutomaticMail.SpamVerdict(headers) is { } spam)
            message.Hold(spam, now);
        else if (AutomaticMail.Reason(headers, message.Subject, message.FromAddress) is { } automatic)
            message.Ignore(automatic, now);
        else if (!await TryReplyTokenAsync(db, message, display, now, cancellationToken)
                 && !await TryHorseAddressAsync(db, message, display, now, cancellationToken))
        {
            await MatchAsync(db, message, display, now, cancellationToken);
        }

        // Placed straight away (a reply, or the horse's own address): its photos and video come with it.
        if (message.StreamItemId is { } itemId && db.ChangeTracker.Entries<StreamItem>().FirstOrDefault(e => e.Entity.Id == itemId)?.Entity is { } item)
            await InboundMedia.AttachAsync(db, inbound, media, message, item, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> TryReplyTokenAsync(PaddocksideDbContext db, InboundMessage message, string display, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tokens = (await OurAddressesAsync(db, message, cancellationToken)).Select(a => a.RecipientToken).OfType<string>().ToList();
        foreach (var token in tokens)
        {
            var routing = await db.RoutingAddresses.SingleOrDefaultAsync(r => r.Kind == RoutingAddressKind.Recipient && r.Token == token, cancellationToken);
            if (routing is not { StreamItemId: { } itemId, PartyId: { } partyId }) continue; // unknown token: falls to tier 2 (§9)

            var original = await db.StreamItems.SingleAsync(i => i.Id == itemId, cancellationToken);
            var horse = await db.Horses.SingleAsync(h => h.Id == original.HorseId, cancellationToken);
            var @event = original.EventId is { } eventId ? await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken) : null;
            var party = await db.Parties.SingleAsync(p => p.Id == partyId, cancellationToken);

            var reply = original.Reply(horse, @event, party.DisplayName, party.Id, "Email", display, message.ReceivedAt);
            db.StreamItems.Add(reply);
            await PromptAnswering.ReadFromReplyAsync(db, original, party, display, message.ReceivedAt, cancellationToken); // "Two please" counts as 2
            @event?.RecordActivity(); // a closed event reopens rather than turning the reply away (D11)

            var delivery = await db.Deliveries.SingleOrDefaultAsync(
                d => d.StreamItemId == itemId && d.PartyId == partyId && d.Channel == DeliveryChannel.Email, cancellationToken);
            delivery?.Record(DeliveryStatus.Replied, message.ReceivedAt);

            message.Place(1, reply, routing.Id, party.Id, display, $"Reply token: {party.DisplayName} replying to a message on {horse.Name}.", now);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHorseAddressAsync(PaddocksideDbContext db, InboundMessage message, string display, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var slugs = (await OurAddressesAsync(db, message, cancellationToken)).Select(a => a.HorseSlugPart).OfType<string>().ToList();
        if (slugs.Count == 0) return false;

        await HorseInboxes.EnsureAsync(db, now, cancellationToken);
        foreach (var slug in slugs)
        {
            var routing = await db.RoutingAddresses.SingleOrDefaultAsync(r => r.Kind == RoutingAddressKind.HorseInbox && r.Token == slug, cancellationToken);
            if (routing is null) continue;

            var horse = await db.Horses.SingleAsync(h => h.Id == routing.HorseId, cancellationToken);
            var candidates = (await db.Events.Where(e => e.HorseId == horse.Id && e.Status == EventStatus.Open).ToListAsync(cancellationToken))
                .Where(e => e.IsInWindow(message.ReceivedAt))
                .ToList();
            var @event = candidates.Count == 1 ? candidates[0] : null;
            var sender = await KnownSenderAsync(db, message.FromAddress, cancellationToken);

            var item = StreamItem.Inbound(horse, @event, message.Subject, display, sender?.DisplayName ?? message.FromName ?? message.FromAddress, sender?.Id, "Email", message.ReceivedAt);
            db.StreamItems.Add(item);
            @event?.RecordActivity();

            var reason = candidates.Count switch
            {
                1 => $"Horse address for {horse.Name}; placed on its one open event in window, {@event!.Title}.",
                0 => $"Horse address for {horse.Name}; no open event in its window, so parked on the horse.",
                _ => $"Horse address for {horse.Name}; {candidates.Count} open events in their window, so parked on the horse.",
            };
            message.Place(2, item, routing.Id, sender?.Id, display, reason, now);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tier 3: score every managed horse against what the mail says and who sent it. The top few candidates are kept
    /// with their reasons; a clear winner becomes a suggestion, anything less waits in the queue.
    /// </summary>
    private async Task MatchAsync(PaddocksideDbContext db, InboundMessage message, string display, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sender = await KnownSenderAsync(db, message.FromAddress, cancellationToken);
        var horses = (await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().ToListAsync(cancellationToken))
            .Where(h => h.CurrentManagementPeriod is not null)
            .ToList();
        var horseIds = horses.Select(h => h.Id).ToList();
        var openEvents = (await db.Events.Where(e => horseIds.Contains(e.HorseId) && e.Status == EventStatus.Open).ToListAsync(cancellationToken))
            .ToLookup(e => e.HorseId);
        var eventIds = openEvents.SelectMany(g => g).Select(e => e.Id).ToList();
        var lastActivity = await db.StreamItems.Where(i => i.EventId != null && eventIds.Contains(i.EventId.Value))
            .GroupBy(i => i.EventId!.Value).Select(g => new { g.Key, Last = g.Max(i => i.OccurredAt) })
            .ToDictionaryAsync(g => g.Key, g => g.Last, cancellationToken);

        // The address it came to is a hint ("faultless@…"); the generic ones ("office@…") simply match nothing.
        var localPart = (await OurAddressesAsync(db, message, cancellationToken)).Select(a => a.LocalPart).FirstOrDefault(l => l is not null);
        var scored = InboundMatcher.Score(message.Subject, display, localPart, sender?.Id,
            horses.Select(h => new MatchHorse(h, openEvents[h.Id].ToList(), lastActivity)).ToList(), message.ReceivedAt);
        foreach (var suggestion in scored.Take(3)) db.MatchCandidates.Add(new MatchCandidate(message, suggestion));

        var names = horses.ToDictionary(h => h.Id, h => h.Name);
        var titles = openEvents.SelectMany(g => g).ToDictionary(e => e.Id, e => e.Title);
        if (InboundMatcher.Winner(scored) is { } best)
        {
            var where = best.EventId is { } eventId ? $"{names[best.HorseId]}, {titles[eventId]}" : $"{names[best.HorseId]} (on the horse)";
            message.Suggest(best.HorseId, best.EventId, $"Looks like {where}: {string.Join("; ", best.Reasons)}.", display, sender?.Id, now);
        }
        else
        {
            message.Park(scored.Count == 0
                ? "Not sent to a reply or horse address, and nothing in it points to one of our horses."
                : $"Not sure where this goes. Closest: {string.Join(", ", scored.Take(3).Select(s => names[s.HorseId]))}.", display, sender?.Id, now);
        }
    }

    /// <summary>The recipient addresses on this tenant's inbound subdomain.</summary>
    private async Task<IReadOnlyList<InboundAddress>> OurAddressesAsync(PaddocksideDbContext db, InboundMessage message, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        return message.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(message.RecipientAddress)
            .Select(a => InboundAddress.Parse(a, email.Value.InboundDomain))
            .OfType<InboundAddress>()
            .Where(a => a.TenantSlug == tenant.Slug)
            .DistinctBy(a => a.Address)
            .ToList();
    }

    /// <summary>Exact address match against PARTY_CONTACT (§3.3); an address two parties share identifies nobody.</summary>
    private static async Task<Party?> KnownSenderAsync(PaddocksideDbContext db, string address, CancellationToken cancellationToken)
    {
        var matches = await db.Parties.Where(p => p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == address)).Take(2).ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<(string Name, string Value)> ReadHeaders(string json) =>
        JsonSerializer.Deserialize<List<HeaderPair>>(json)?.Select(h => (h.Name, h.Value)).ToList() ?? [];

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = LineBreaks().Replace(html, "\n");
        text = Tags().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text);
    }

    private sealed record HeaderPair(string Name, string Value);

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
}
