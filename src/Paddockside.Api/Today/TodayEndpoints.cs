using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Today;

/// <summary>
/// Today, the staff home (search-reporting.md §3): "Runners and trials today and tomorrow with their event stage;
/// important messages not yet delivered to everyone; pending queue count and oldest age; dormant events; drafts
/// awaiting open; integration health. Nothing else." The screen a racing manager opens at 7 am.
/// </summary>
public static class TodayEndpoints
{
    /// <summary>How long an open event can go without a new item before it counts as dormant. Event types will
    /// carry their own dormancy period (event-library.md §2); until then, one period for every type.</summary>
    public static readonly TimeSpan Dormancy = TimeSpan.FromDays(14);

    /// <summary>Delivery can take a few minutes; only after this is a message that hasn't arrived worth a look.</summary>
    public static readonly TimeSpan DeliveryGrace = TimeSpan.FromMinutes(30);

    /// <param name="Time">The start time, when the key date carries one ("3:05 pm").</param>
    /// <param name="Stage">The last step done ("Accepted"), or null if none yet.</param>
    /// <param name="Attention">Steps not received, in words ("Trainer's pre-race not received").</param>
    public sealed record Runner(Guid EventId, Guid HorseId, string Horse, string Type, string Title, string? Time, string? Stage,
        IReadOnlyList<HorseEndpoints.StepView> Steps, IReadOnlyList<string> Attention);

    /// <param name="Detail">"2 of 5 not delivered: 1 bounced, 1 waiting to send".</param>
    public sealed record Undelivered(Guid ItemId, Guid? EventId, Guid HorseId, string Horse, string? Event, string Title, string Sent, string Detail);

    public sealed record QueueState(int Waiting, string? Oldest);

    public sealed record QuietEvent(Guid Id, string Horse, string Type, string Title, string Detail);

    /// <param name="State">"ok", "warn" or "off".</param>
    public sealed record Health(string Label, string State, string Detail);

    /// <summary>A horse whose shares add up to more than the whole horse: a mistake that must be put right.</summary>
    public sealed record OverAllocated(Guid HorseId, string Horse, string Allocated);

    public sealed record TodayPage(string Date, IReadOnlyList<Runner> Today, IReadOnlyList<Runner> Tomorrow, IReadOnlyList<Undelivered> Undelivered,
        QueueState Queue, IReadOnlyList<QuietEvent> Dormant, IReadOnlyList<QuietEvent> Drafts, IReadOnlyList<Health> Health,
        IReadOnlyList<OverAllocated>? OverAllocated = null);

    private static readonly string[] RunnerTypes = [EventTypes.RaceStart.Code, EventTypes.BarrierTrial.Code];

    public static void MapTodayEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/today", Get).RequireAuthorization(StaffPolicy.Name);

    private static async Task<TodayPage> Get(PaddocksideDbContext db, IEmailSender email, ISmsSender texts, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var today = Words.FromLocal(Words.Local(now).Date);
        var dayAfterTomorrow = today.AddDays(2);

        var events = await db.Events.AsNoTracking()
            .Where(e => e.Status == EventStatus.Draft || e.Status == EventStatus.Open
                || (RunnerTypes.Contains(e.EventType) && e.KeyDate >= today && e.KeyDate < dayAfterTomorrow && e.Status != EventStatus.Cancelled))
            .ToListAsync(cancellationToken);
        var eventIds = events.Select(e => e.Id).ToList();
        var activity = await db.StreamItems.AsNoTracking().Where(i => i.EventId != null && eventIds.Contains(i.EventId.Value))
            .Select(i => new { EventId = i.EventId!.Value, i.StepCode, i.OccurredAt }).ToListAsync(cancellationToken);
        var byEvent = activity.ToLookup(a => a.EventId);

        // Important owner messages from the last three days that haven't reached everyone.
        var since = now.AddDays(-3);
        var recent = await db.StreamItems.AsNoTracking()
            .Where(i => i.Kind == StreamItemKind.Message && i.Direction == StreamItemDirection.Outbound && i.OccurredAt >= since)
            .ToListAsync(cancellationToken);
        var recentIds = recent.Select(i => i.Id).ToList();
        var deliveries = (await db.Deliveries.AsNoTracking()
            .Where(d => recentIds.Contains(d.StreamItemId) && !d.Direct && d.Channel != DeliveryChannel.Portal)
            .ToListAsync(cancellationToken)).ToLookup(d => d.StreamItemId);

        var horseIds = events.Select(e => e.HorseId).Concat(recent.Select(i => i.HorseId)).Distinct().ToList();
        var horses = await db.Horses.AsNoTracking().Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
        string HorseName(Guid id) => horses.GetValueOrDefault(id) ?? "Unknown horse";
        var eventById = events.ToDictionary(e => e.Id);
        var otherEventIds = recent.Where(i => i.EventId is { } id && !eventById.ContainsKey(id)).Select(i => i.EventId!.Value).Distinct().ToList();
        foreach (var e in await db.Events.AsNoTracking().Where(e => otherEventIds.Contains(e.Id)).ToListAsync(cancellationToken)) eventById[e.Id] = e;

        Runner ToRunner(DomainEvent e)
        {
            var mine = byEvent[e.Id].ToList();
            var summary = HorseEndpoints.Summarise(e, mine.Select(a => a.StepCode), mine.Count == 0 ? null : mine.Max(a => a.OccurredAt), now);
            var local = Words.Local(e.KeyDate!.Value);
            return new Runner(e.Id, e.HorseId, HorseName(e.HorseId), summary.Type, e.Title,
                local.TimeOfDay == TimeSpan.Zero ? null : Words.Time(e.KeyDate.Value),
                summary.Steps.LastOrDefault(s => s.State == "Done")?.Label,
                summary.Steps,
                summary.Steps.Where(s => s.State == "Missing").Select(s => $"{s.Label} not received").ToList());
        }

        var runners = events.Where(e => RunnerTypes.Contains(e.EventType) && e.KeyDate >= today && e.KeyDate < dayAfterTomorrow && e.Status != EventStatus.Draft)
            .OrderBy(e => e.KeyDate).ToList();

        var undelivered = recent
            .Where(i => NotificationCategories.For(i, i.EventId is { } id ? eventById.GetValueOrDefault(id) : null).Important)
            .Select(i => (Item: i, Missing: deliveries[i.Id].Where(d => NotArrived(d, now)).ToList(), All: deliveries[i.Id].Count()))
            .Where(x => x.Missing.Count > 0)
            .OrderByDescending(x => x.Item.OccurredAt)
            .Select(x => new Undelivered(x.Item.Id, x.Item.EventId, x.Item.HorseId, HorseName(x.Item.HorseId),
                x.Item.EventId is { } id && eventById.TryGetValue(id, out var ev) ? ev.Title : null,
                x.Item.Title ?? Excerpt(x.Item.Body),
                Words.Recently(x.Item.OccurredAt, now),
                $"{x.Missing.Count} of {x.All} not delivered: {Reasons(x.Missing)}"))
            .ToList();

        var waiting = await db.InboundMessages.Where(m => m.State == InboundState.Pending || m.State == InboundState.Suggested)
            .Select(m => m.ReceivedAt).ToListAsync(cancellationToken);

        var dormant = events.Where(e => e.Status == EventStatus.Open)
            .Select(e => (Event: e, Last: byEvent[e.Id].Select(a => (DateTimeOffset?)a.OccurredAt).Max() ?? e.OpenedAt))
            .Where(x => x.Last is { } last && now - last > Dormancy && !(x.Event.KeyDate > now))
            .OrderBy(x => x.Last)
            .Select(x => new QuietEvent(x.Event.Id, HorseName(x.Event.HorseId), EventTypes.Find(x.Event.EventType).Name, x.Event.Title,
                byEvent[x.Event.Id].Any() ? $"Nothing new since {Words.Day(x.Last!.Value, now)}" : $"Opened {Words.Day(x.Last!.Value, now)}, nothing posted"))
            .ToList();

        var drafts = events.Where(e => e.Status == EventStatus.Draft)
            .OrderBy(e => e.KeyDate ?? DateTimeOffset.MaxValue)
            .Select(e => new QuietEvent(e.Id, HorseName(e.HorseId), EventTypes.Find(e.EventType).Name, e.Title,
                e.KeyDate is { } key ? Words.Day(key, now) : "No date set"))
            .ToList();

        return new TodayPage(
            Words.Local(now).ToString("dddd d MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-AU")),
            runners.Where(e => e.KeyDate < today.AddDays(1)).Select(ToRunner).ToList(),
            runners.Where(e => e.KeyDate >= today.AddDays(1)).Select(ToRunner).ToList(),
            undelivered,
            new QueueState(waiting.Count, waiting.Count == 0 ? null : Words.Recently(waiting.Min(), now)),
            dormant,
            drafts,
            await HealthAsync(db, email, texts, now, cancellationToken),
            (await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().ToListAsync(cancellationToken))
                .Where(h => h.AllocatedUnits > Horse.WholeUnits)
                .OrderBy(h => h.Name)
                .Select(h => new OverAllocated(h.Id, h.Name, Horse.Percent(h.AllocatedUnits)))
                .ToList());
    }

    private static string Excerpt(string body) => body.Length <= 60 ? body : $"{body[..60].TrimEnd()}…";

    /// <summary>Still to arrive after the grace period, or refused for good.</summary>
    private static bool NotArrived(Delivery d, DateTimeOffset now) =>
        d.Status is DeliveryStatus.Bounced or DeliveryStatus.Suppressed
        || (d.Status is DeliveryStatus.Queued or DeliveryStatus.Sent && now - d.StatusAt > DeliveryGrace);

    private static string Reasons(IEnumerable<Delivery> missing) => string.Join(", ", missing
        .GroupBy(d => d.Status switch
        {
            DeliveryStatus.Bounced => "bounced",
            DeliveryStatus.Suppressed => "not sent (address blocked)",
            DeliveryStatus.Queued => "waiting to send",
            _ => "sent, no delivery report",
        })
        .Select(g => $"{g.Count()} {g.Key}"));

    /// <summary>Email out, email in and texts: working, needing a look, or not set up.</summary>
    private static async Task<IReadOnlyList<Health>> HealthAsync(PaddocksideDbContext db, IEmailSender email, ISmsSender texts, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dayAgo = now.AddDays(-1);
        var bounced = await db.Deliveries.CountAsync(d => d.Channel == DeliveryChannel.Email && d.Status == DeliveryStatus.Bounced && d.StatusAt >= dayAgo, cancellationToken);
        var lastIn = await db.InboundMessages.Where(m => m.Channel == "Email").MaxAsync(m => (DateTimeOffset?)m.ReceivedAt, cancellationToken);

        return
        [
            !email.IsConfigured ? new Health("Email to owners", "off", "Not set up: nothing can be sent.")
                : bounced > 0 ? new Health("Email to owners", "warn", $"{bounced} bounced in the last day.")
                : new Health("Email to owners", "ok", "Sending normally."),
            lastIn is { } last ? new Health("Email coming in", "ok", $"Last message: {Words.Recently(last, now)}.")
                : new Health("Email coming in", "warn", "Nothing received yet."),
            texts.IsConfigured ? new Health("Texts", "ok", "Sending and receiving.")
                : new Health("Texts", "off", "Not set up yet: important news goes by email instead."),
        ];
    }
}
