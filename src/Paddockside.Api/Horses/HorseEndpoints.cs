using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Inbound;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Horses;

/// <summary>
/// The tenant's horses and each horse's timeline (search-reporting.md §3). The tenant filter decides which rows
/// exist; another tenant's horse is simply not found.
/// </summary>
public static class HorseEndpoints
{
    public sealed record HorseSummary(Guid Id, string Name, bool Managed, DateTimeOffset? ManagedSince, int CurrentOwners);

    public sealed record HorseDetail(
        Guid Id,
        string Name,
        string? SexAge,
        string? Pedigree,
        bool Managed,
        string? ManagedSince,
        int CurrentOwners,
        string? NextKeyDate,
        string? InboxAddress);

    public sealed record StepView(string Label, string State, string? Detail, bool ClientVisible);

    public sealed record EventSummary(
        Guid Id,
        string Type,
        string Title,
        string KeyDate,
        string Status,
        IReadOnlyList<StepView> Steps,
        string LastActivity);

    public static void MapHorseEndpoints(this IEndpointRouteBuilder app)
    {
        var horses = app.MapGroup("/api/horses").RequireAuthorization(StaffPolicy.Name);
        horses.MapGet("/", ListHorses);
        horses.MapGet("/{id:guid}", GetHorse);
        horses.MapGet("/{id:guid}/events", ListEvents);
    }

    private static async Task<IReadOnlyList<HorseSummary>> ListHorses(PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var horses = await LoadHorses(db).ToListAsync(cancellationToken);

        return horses
            .Select(h => new HorseSummary(h.Id, h.Name, h.CurrentManagementPeriod is not null, h.CurrentManagementPeriod?.From, CurrentOwners(h)))
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IResult> GetHorse(Guid id, PaddocksideDbContext db, TimeProvider clock, IOptions<EmailOptions> email, CancellationToken cancellationToken)
    {
        var horse = await LoadHorses(db).SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();

        var now = clock.GetUtcNow();
        var nextKeyDate = await db.Events.AsNoTracking()
            .Where(e => e.HorseId == id && e.Status == EventStatus.Open && e.KeyDate != null && e.KeyDate >= now.AddHours(-12))
            .OrderBy(e => e.KeyDate)
            .Select(e => new { e.KeyDate, e.Title })
            .FirstOrDefaultAsync(cancellationToken);

        // The horse's own email address (messaging-channels.md §3.1), for staff to give to the trainer.
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var addresses = await db.RoutingAddresses.AsNoTracking()
            .Where(r => r.Kind == RoutingAddressKind.HorseInbox && r.HorseId == id)
            .ToListAsync(cancellationToken);
        var inbox = tenant.Slug.Length > 0 ? HorseInboxes.Current(horse, addresses)?.EmailAddress(tenant, email.Value.InboundDomain) : null;

        return Results.Ok(new HorseDetail(
            horse.Id,
            horse.Name,
            SexAge(horse, now),
            horse.Sire is not null && horse.Dam is not null ? $"{horse.Sire} x {horse.Dam}" : null,
            horse.CurrentManagementPeriod is not null,
            horse.CurrentManagementPeriod is { } period ? Words.Day(period.From, now) : null,
            CurrentOwners(horse),
            nextKeyDate is null ? null : $"{Words.Day(nextKeyDate.KeyDate!.Value, now)} · {nextKeyDate.Title}",
            inbox));
    }

    /// <summary>The horse's events, newest key date first: the horse timeline.</summary>
    private static async Task<IResult> ListEvents(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!await db.Horses.AnyAsync(h => h.Id == id, cancellationToken)) return Results.NotFound();

        var events = await db.Events.AsNoTracking().Where(e => e.HorseId == id).ToListAsync(cancellationToken);
        var eventIds = events.Select(e => e.Id).ToList();
        var activity = await db.StreamItems.AsNoTracking()
            .Where(i => i.EventId != null && eventIds.Contains(i.EventId.Value))
            .Select(i => new { EventId = i.EventId!.Value, i.StepCode, i.OccurredAt })
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();
        return Results.Ok(events
            .OrderByDescending(e => e.KeyDate ?? e.OpenedAt)
            .Select(e =>
            {
                var items = activity.Where(a => a.EventId == e.Id).ToList();
                return Summarise(e, items.Select(a => a.StepCode), items.Count == 0 ? null : items.Max(a => a.OccurredAt), now);
            })
            .ToList());
    }

    /// <summary>Shared with the event page, so the card and the page header always agree.</summary>
    public static EventSummary Summarise(DomainEvent e, IEnumerable<string?> stepCodes, DateTimeOffset? lastActivity, DateTimeOffset now)
    {
        var type = EventTypes.Find(e.EventType);
        var finished = e.Status is EventStatus.Closed or EventStatus.Cancelled;
        var stages = EventTypes.Stages(type, e.KeyDate, stepCodes, now, finished);

        return new EventSummary(
            e.Id,
            type.Name,
            e.Title,
            e.KeyDate is { } key ? Words.Day(key, now) : "No date set",
            e.Status.ToString(),
            stages.Select(s => new StepView(s.Step.Label, s.State.ToString(), Due(s, e.KeyDate, now), s.Step.ClientVisible)).ToList(),
            e.Status switch
            {
                EventStatus.Closed => $"Closed {(e.ClosedAt is { } c ? Words.Day(c, now) : "")}".Trim(),
                EventStatus.Cancelled => "Cancelled",
                _ => lastActivity is { } at ? $"Last update {Words.Recently(at, now)}" : "Nothing posted yet",
            });
    }

    private static string? Due(Stage stage, DateTimeOffset? keyDate, DateTimeOffset now) =>
        stage.State is StageState.Current or StageState.Missing && keyDate is { } key
            ? $"due {Words.Local(key.AddDays(stage.Step.DaysFromKeyDate)).ToString("ddd d MMM", System.Globalization.CultureInfo.GetCultureInfo("en-AU"))}"
            : null;

    private static IQueryable<Horse> LoadHorses(PaddocksideDbContext db) =>
        db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery();

    private static int CurrentOwners(Horse h) =>
        h.CurrentManagementPeriod is { } period
            ? h.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.PartyId).Distinct().Count()
            : 0;

    private static string? SexAge(Horse h, DateTimeOffset now)
    {
        var age = h.AgeOn(DateOnly.FromDateTime(Words.Local(now).DateTime));
        var sex = h.Sex?.ToString().ToLowerInvariant();
        return (age, sex) switch
        {
            (null, null) => null,
            (null, _) => char.ToUpperInvariant(sex![0]) + sex[1..],
            (0, _) => $"Foal{(sex is null ? "" : $" ({sex})")}",
            (1, _) => $"Yearling{(sex is null ? "" : $" {sex}")}",
            _ => $"{age}yo{(sex is null ? "" : $" {sex}")}",
        };
    }
}
