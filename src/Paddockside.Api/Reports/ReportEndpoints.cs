using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Reports;

/// <summary>
/// The first two staff reports (search-reporting.md §4), the ones that answer "did everyone get told":
/// <b>Delivery by event</b> — who was told what about this race, and did they open it — and <b>Unreached owners</b> —
/// which owners have not opened any of the last few important messages, across all horses. Both download as CSV.
/// Opened means any sign they saw it: the email was opened, they read it in the portal, or they replied.
/// </summary>
public static class ReportEndpoints
{
    /// <param name="Summary">"3 of 4 opened".</param>
    public sealed record SentItem(Guid Id, string Title, string Sent, string Summary);

    /// <param name="State">Opened, Replied, Delivered, Sent, Waiting, Bounced, NotSent, PortalOnly, or None (not an owner then).</param>
    /// <param name="Detail">The channels, or why it didn't arrive ("Mailbox full").</param>
    public sealed record Cell(string State, string? Detail);

    public sealed record OwnerLine(Guid PartyId, string Name, string? Email, string? Mobile, bool CurrentOwner, IReadOnlyList<Cell> Cells);

    public sealed record DeliveryReport(Guid EventId, Guid HorseId, string Horse, string Event, IReadOnlyList<SentItem> Items, IReadOnlyList<OwnerLine> Owners);

    /// <param name="Horses">"Bel Esprit, Faultless Miss".</param>
    /// <param name="Missed">How many of their last important messages went unopened (all of those looked at).</param>
    /// <param name="Why">What became of them: "Emails bounced: mailbox full", "Delivered, never opened".</param>
    public sealed record Unreached(Guid PartyId, string Name, string? Email, string? Mobile, string Horses, int Missed, string LastSent, string Why);

    public sealed record UnreachedReport(int Last, int Days, int Checked, IReadOnlyList<Unreached> Owners);

    public const int DefaultLast = 3;

    public const int DefaultDays = 90;

    private static readonly DeliveryStatus[] Seen = [DeliveryStatus.Opened, DeliveryStatus.Replied];

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/reports").RequireAuthorization(StaffPolicy.Name);
        reports.MapGet("/delivery/{eventId:guid}", async (Guid eventId, PaddocksideDbContext db, TimeProvider clock, CancellationToken ct) =>
            await DeliveryAsync(eventId, db, clock.GetUtcNow(), ct) is { } report ? Results.Ok(report) : Results.NotFound());
        reports.MapGet("/delivery/{eventId:guid}/export.csv", ExportDelivery);
        reports.MapGet("/unreached", async (int? last, int? days, PaddocksideDbContext db, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(await UnreachedAsync(last, days, db, clock.GetUtcNow(), ct)));
        reports.MapGet("/unreached/export.csv", ExportUnreached);
    }

    private static async Task<DeliveryReport?> DeliveryAsync(Guid eventId, PaddocksideDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var e = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (e is null) return null;
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleAsync(h => h.Id == e.HorseId, cancellationToken);

        var items = (await db.StreamItems.AsNoTracking().Where(i => i.EventId == eventId).ToListAsync(cancellationToken))
            .Where(SentToOwners).OrderBy(i => i.OccurredAt).ToList();
        var itemIds = items.Select(i => i.Id).ToList();
        var deliveries = (await db.Deliveries.AsNoTracking().Where(d => itemIds.Contains(d.StreamItemId) && !d.Direct).ToListAsync(cancellationToken))
            .ToLookup(d => (d.StreamItemId, d.PartyId));
        var (reads, replied) = await SignsAsync(db, itemIds, cancellationToken);

        // Everyone who was sent something here, and everyone who owns the horse now.
        var current = CurrentOwners(tenant, horse).ToHashSet();
        var partyIds = deliveries.Select(g => g.Key.PartyId).Concat(current).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToListAsync(cancellationToken);

        var lines = parties
            .Select(p => new OwnerLine(p.Id, p.DisplayName, p.PrimaryEmail?.Value, p.PrimaryMobile?.Value, current.Contains(p.Id),
                items.Select(i => CellFor(deliveries[(i.Id, p.Id)].ToList(), reads.Contains((i.Id, p.Id)), replied.Contains((i.Id, p.Id)),
                    AccessRule.CanSee(new Viewer(tenant.Id, AudienceClass.Client, p.Id), tenant, horse, i))).ToList()))
            .OrderByDescending(l => l.CurrentOwner).ThenBy(l => l.Name)
            .ToList();

        var sent = items.Select((i, n) =>
        {
            var told = lines.Count(l => l.Cells[n].State != "None");
            var opened = lines.Count(l => l.Cells[n].State is "Opened" or "Replied");
            return new SentItem(i.Id, Title(i), Words.DayAndTime(i.OccurredAt, now), $"{opened} of {told} opened");
        }).ToList();

        return new DeliveryReport(e.Id, horse.Id, horse.Name, e.Title, sent, lines);
    }

    private static async Task<IResult> ExportDelivery(Guid eventId, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await DeliveryAsync(eventId, db, clock.GetUtcNow(), cancellationToken) is not { } report) return Results.NotFound();
        IEnumerable<string> header = ["Owner", "Email", "Mobile", "Owns it now", .. report.Items.Select(i => Csv.Cell($"{i.Title} ({i.Sent})"))];
        var rows = report.Owners.Select(o => (IEnumerable<string>)[Csv.Cell(o.Name), Csv.Cell(o.Email), Csv.Cell(o.Mobile), o.CurrentOwner ? "Yes" : "No",
            .. o.Cells.Select(c => Csv.Cell(c.Detail is null ? StateWords(c.State) : $"{StateWords(c.State)}: {c.Detail}"))]);
        return Csv.File(rows.Prepend(header), $"delivery-{Slug(report.Horse)}-{Slug(report.Event)}.csv");
    }

    private static async Task<UnreachedReport> UnreachedAsync(int? last, int? days, PaddocksideDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var n = Math.Clamp(last ?? DefaultLast, 1, 20);
        var period = Math.Clamp(days ?? DefaultDays, 7, 730);
        var since = now.AddDays(-period);

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var horses = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => h.Interests.Any(i => i.State == InterestState.Active))
            .ToListAsync(cancellationToken);
        var owned = horses.SelectMany(h => CurrentOwners(tenant, h).Select(p => (Horse: h, PartyId: p))).ToList();
        if (owned.Count == 0) return new UnreachedReport(n, period, 0, []);

        var horseIds = horses.Select(h => h.Id).ToList();
        var candidates = (await db.StreamItems.AsNoTracking().Where(i => horseIds.Contains(i.HorseId) && i.OccurredAt >= since).ToListAsync(cancellationToken))
            .Where(SentToOwners).ToList();
        var eventIds = candidates.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        DomainEvent? EventOf(StreamItem i) => i.EventId is { } id ? events.GetValueOrDefault(id) : null;
        var important = candidates.Where(i => NotificationCategories.For(i, EventOf(i)).Important).ToList();
        var byHorse = important.ToLookup(i => i.HorseId);

        var itemIds = important.Select(i => i.Id).ToList();
        var deliveries = (await db.Deliveries.AsNoTracking().Where(d => itemIds.Contains(d.StreamItemId) && !d.Direct).ToListAsync(cancellationToken))
            .ToLookup(d => (d.StreamItemId, d.PartyId));
        var (reads, replied) = await SignsAsync(db, itemIds, cancellationToken);

        var partyIds = owned.Select(o => o.PartyId).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var looked = 0;
        var unreached = new List<Unreached>();
        foreach (var owner in owned.GroupBy(o => o.PartyId))
        {
            // Their last few important messages, across their horses, from while they owned them.
            var theirs = owner
                .SelectMany(o => byHorse[o.Horse.Id].Where(i => i.OccurredAt >= OwnerSince(o.Horse, o.PartyId)
                    && AccessRule.CanSee(new Viewer(tenant.Id, AudienceClass.Client, o.PartyId), tenant, o.Horse, i)))
                .OrderByDescending(i => i.OccurredAt).Take(n).ToList();
            if (theirs.Count == 0) continue;
            looked++;

            var opened = theirs.Any(i => reads.Contains((i.Id, owner.Key)) || replied.Contains((i.Id, owner.Key)) || deliveries[(i.Id, owner.Key)].Any(d => Seen.Contains(d.Status)));
            if (opened || !parties.TryGetValue(owner.Key, out var party)) continue;

            var sentToThem = theirs.SelectMany(i => deliveries[(i.Id, owner.Key)]).ToList();
            unreached.Add(new Unreached(party.Id, party.DisplayName, party.PrimaryEmail?.Value, party.PrimaryMobile?.Value,
                string.Join(", ", owner.Select(o => o.Horse.Name).Distinct().Order()), theirs.Count, Words.Day(theirs[0].OccurredAt, now),
                Why(sentToThem, now)));
        }

        return new UnreachedReport(n, period, looked, unreached.OrderBy(u => u.Name).ToList());
    }

    private static async Task<IResult> ExportUnreached(int? last, int? days, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var report = await UnreachedAsync(last, days, db, clock.GetUtcNow(), cancellationToken);
        IEnumerable<string> header = ["Owner", "Email", "Mobile", "Horses", "Unopened", "Last sent", "What happened"];
        var rows = report.Owners.Select(o => (IEnumerable<string>)[Csv.Cell(o.Name), Csv.Cell(o.Email), Csv.Cell(o.Mobile), Csv.Cell(o.Horses),
            o.Missed.ToString(System.Globalization.CultureInfo.InvariantCulture), Csv.Cell(o.LastSent), Csv.Cell(o.Why)]);
        return Csv.File(rows.Prepend(header), "unreached-owners.csv");
    }

    /// <summary>What owners are sent: staff messages and media, and facts, that some owner may see.</summary>
    private static bool SentToOwners(StreamItem i) =>
        i.IsSentToOwners && i.Scope is StreamItemScope.Owners or StreamItemScope.OwnersAtTheTime or StreamItemScope.NamedParties;

    /// <summary>The parties with a live share now (not the house share, not a share still to confirm).</summary>
    private static IEnumerable<Guid> CurrentOwners(Tenant tenant, Horse horse) =>
        horse.CurrentManagementPeriod is { } period
            ? horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive && i.PartyId != tenant.HousePartyId).Select(i => i.PartyId).Distinct()
            : [];

    /// <summary>When their current holding in the horse began: messages from before are not theirs to have missed.</summary>
    private static DateTimeOffset OwnerSince(Horse horse, Guid partyId) =>
        horse.Interests.Where(i => i.PartyId == partyId && i.IsActive).Select(i => i.EffectiveFrom).DefaultIfEmpty(DateTimeOffset.MinValue).Min();

    /// <summary>Who read which of these items in the portal, and who replied to which (from any channel).</summary>
    private static async Task<(HashSet<(Guid Item, Guid Party)> Read, HashSet<(Guid Item, Guid Party)> Replied)> SignsAsync(
        PaddocksideDbContext db, List<Guid> itemIds, CancellationToken cancellationToken)
    {
        var reads = await db.ItemReads.AsNoTracking().Where(r => itemIds.Contains(r.StreamItemId))
            .Select(r => new { r.StreamItemId, r.PartyId }).ToListAsync(cancellationToken);
        var replies = await db.StreamItems.AsNoTracking()
            .Where(i => i.InReplyToId != null && itemIds.Contains(i.InReplyToId.Value) && i.AuthorPartyId != null)
            .Select(i => new { Item = i.InReplyToId!.Value, Party = i.AuthorPartyId!.Value }).ToListAsync(cancellationToken);
        return (reads.Select(r => (r.StreamItemId, r.PartyId)).ToHashSet(), replies.Select(r => (r.Item, r.Party)).ToHashSet());
    }

    private static Cell CellFor(IReadOnlyList<Delivery> deliveries, bool read, bool replied, bool canSee)
    {
        var channels = deliveries.Count == 0 ? null : string.Join(" · ", deliveries.Select(d => Channel(d.Channel)).Distinct());
        if (replied || deliveries.Any(d => d.Status == DeliveryStatus.Replied)) return new("Replied", channels);
        if (read || deliveries.Any(d => d.Status == DeliveryStatus.Opened)) return new("Opened", read && deliveries.Count == 0 ? "Portal" : channels);
        if (deliveries.Count == 0) return canSee ? new("PortalOnly", null) : new("None", null);

        var best = deliveries.OrderBy(d => Rank(d.Status)).First();
        return best.Status switch
        {
            DeliveryStatus.Delivered => new("Delivered", channels),
            DeliveryStatus.Sent => new("Sent", channels),
            DeliveryStatus.Queued => new("Waiting", channels),
            DeliveryStatus.Bounced => new("Bounced", best.Note ?? channels),
            _ => new("NotSent", best.Note ?? channels),
        };
    }

    /// <summary>Best news first, for picking the one state to show when a message went by email and text.</summary>
    private static int Rank(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Delivered => 0,
        DeliveryStatus.Sent => 1,
        DeliveryStatus.Queued => 2,
        DeliveryStatus.Bounced => 3,
        _ => 4,
    };

    private static string Why(IReadOnlyList<Delivery> sent, DateTimeOffset now)
    {
        if (sent.Count == 0) return "Not sent: they get nothing by email or text, and haven't looked in the portal";
        var latest = sent.OrderByDescending(d => d.StatusAt).First();
        return latest.Status switch
        {
            DeliveryStatus.Bounced => $"Bounced{(latest.Note is null ? "" : $": {latest.Note}")}",
            DeliveryStatus.Suppressed => $"Not sent{(latest.Note is null ? "" : $": {latest.Note}")}",
            DeliveryStatus.Queued => "Still waiting to send",
            _ => sent.Any(d => d.Status == DeliveryStatus.Delivered) ? "Delivered, never opened" : "Sent, never opened",
        };
    }

    private static string StateWords(string state) => state switch
    {
        "PortalOnly" => "In the portal, not sent",
        "NotSent" => "Not sent",
        "None" => "Not an owner then",
        "Waiting" => "Waiting to send",
        _ => state,
    };

    private static string Channel(DeliveryChannel channel) => channel switch
    {
        DeliveryChannel.Sms => "Text",
        DeliveryChannel.Portal => "Portal",
        _ => "Email",
    };

    private static string Title(StreamItem i) =>
        i.Title ?? (i.Body.Length <= 60 ? i.Body : i.Body[..57].TrimEnd() + "…");

    private static string Slug(string text) =>
        string.Join('-', text.ToLowerInvariant().Split(text.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries));
}
