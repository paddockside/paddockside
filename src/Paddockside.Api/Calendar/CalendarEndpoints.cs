using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Calendar;

/// <summary>
/// One calendar of everything with a date (David, 19 Nov 2026): every horse's events on their key date — race starts,
/// trials, vet visits, the rest — and horses due back from a spell or injury. The horses page shows the horse kinds;
/// social events join as another kind later, and a unified calendar shows them all. Cancelled events are left off;
/// drafts are included, marked, so races being planned show.
/// </summary>
public static class CalendarEndpoints
{
    /// <param name="Date">yyyy-MM-dd, Sydney time.</param>
    /// <param name="Time">"3:05 pm" when the key date has a time.</param>
    /// <param name="Kind">RaceStart, BarrierTrial, Veterinary, General, DueBack, or Social.</param>
    /// <param name="Short">For a crowded day square: "Faultless Miss · Randwick, Race 6".</param>
    /// <param name="Detail">Where it's up to: "next: Accepted", "Spelling at Riverbank Farm".</param>
    /// <param name="Href">What it opens.</param>
    public sealed record CalendarEntry(string Date, string? Time, string Kind, string KindLabel, Guid? HorseId, string? Horse, string Title, string Short,
        string? Detail, string Href, bool Draft = false, bool Done = false);

    /// <summary>A month and its edges is the most anyone looks at; more is refused, so a slip can't load years.</summary>
    private const int MaxDays = 62;

    public static void MapCalendarEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/calendar", Get).RequireAuthorization(StaffPolicy.Name);

    private static async Task<IResult> Get(string from, string to, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) || last < first)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Give the dates as from=yyyy-MM-dd&to=yyyy-MM-dd.");
        if (last.DayNumber - first.DayNumber > MaxDays)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Up to {MaxDays} days at a time.");

        var now = clock.GetUtcNow();
        var start = Words.FromLocal(first.ToDateTime(TimeOnly.MinValue));
        var end = Words.FromLocal(last.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var events = await db.Events.AsNoTracking()
            .Where(e => e.KeyDate != null && e.KeyDate >= start && e.KeyDate < end && e.Status != EventStatus.Cancelled)
            .ToListAsync(cancellationToken);
        var statuses = (await db.HorseStatuses.AsNoTracking().ToListAsync(cancellationToken))
            .GroupBy(s => s.HorseId).Select(g => g.OrderByDescending(s => s.SetAt).First())
            .Where(s => s.ExpectedOn is { } on && on >= first && on <= last && s.Status is not (HorseStatus.InWork or HorseStatus.Racing or HorseStatus.Retired))
            .ToList();

        // Social events: on every day they run (a tour shows on each), cancelled ones left off.
        var socials = await db.SocialEvents.AsNoTracking()
            .Where(s => s.Status != SocialStatus.Cancelled && s.StartsAt < end && (s.EndsAt ?? s.StartsAt) >= start)
            .ToListAsync(cancellationToken);
        var socialIds = socials.Select(s => s.Id).ToList();
        var coming = (await db.SocialAttendees.AsNoTracking().Where(a => socialIds.Contains(a.SocialEventId) && a.Status == AttendeeStatus.Coming)
            .GroupBy(a => a.SocialEventId).Select(g => new { g.Key, Places = g.Sum(a => a.Places) }).ToListAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Places);

        var horseIds = events.Select(e => e.HorseId).Concat(statuses.Select(s => s.HorseId)).Distinct().ToList();
        var horses = await db.Horses.AsNoTracking().Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
        var eventIds = events.Select(e => e.Id).ToList();
        var steps = (await db.StreamItems.AsNoTracking().Where(i => i.EventId != null && eventIds.Contains(i.EventId.Value))
            .Select(i => new { EventId = i.EventId!.Value, i.StepCode, i.OccurredAt }).ToListAsync(cancellationToken)).ToLookup(i => i.EventId);

        var entries = events.Select(e =>
        {
            var local = Words.Local(e.KeyDate!.Value);
            var type = EventTypes.Find(e.EventType);
            var summary = HorseEndpoints.Summarise(e, steps[e.Id].Select(s => s.StepCode), steps[e.Id].Select(s => (DateTimeOffset?)s.OccurredAt).Max(), now);
            var next = summary.Steps.FirstOrDefault(s => s.State is "Current" or "Missing");
            var horse = horses.GetValueOrDefault(e.HorseId) ?? "Unknown horse";
            return new CalendarEntry(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.TimeOfDay == TimeSpan.Zero ? null : Words.Time(e.KeyDate.Value),
                e.EventType, type.Name, e.HorseId, horse, e.Title, $"{horse} · {e.Title}",
                e.Status == EventStatus.Closed ? "Closed" : next is null ? null : $"next: {next.Label}",
                $"events/{e.Id}", Draft: e.Status == EventStatus.Draft, Done: e.Status == EventStatus.Closed);
        }).Concat(statuses.Select(s =>
        {
            var horse = horses.GetValueOrDefault(s.HorseId) ?? "Unknown horse";
            return new CalendarEntry(s.ExpectedOn!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, "DueBack", "Due back", s.HorseId, horse,
                $"{horse} due back", $"{horse} · due back", s.Location is null ? HorseStatusEntry.Label(s.Status) : $"{HorseStatusEntry.Label(s.Status)} at {s.Location}",
                $"horses/{s.HorseId}");
        })).Concat(socials.SelectMany(s =>
        {
            var local = Words.Local(s.StartsAt);
            var firstDay = DateOnly.FromDateTime(local.Date);
            var lastDay = DateOnly.FromDateTime(Words.Local(s.EndsAt ?? s.StartsAt).Date);
            var count = coming.GetValueOrDefault(s.Id);
            var detail = s.Places is { } places ? $"{count} of {places} places" : $"{count} coming";
            return Enumerable.Range(0, lastDay.DayNumber - firstDay.DayNumber + 1).Select(n => firstDay.AddDays(n)).Where(d => d >= first && d <= last)
                .Select(d => new CalendarEntry(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    d == firstDay && local.TimeOfDay != TimeSpan.Zero ? Words.Time(s.StartsAt) : null,
                    "Social", SocialEvent.Label(s.Kind), null, null, s.Title, s.Title,
                    s.Venue is null ? detail : $"{s.Venue} · {detail}", $"social/{s.Id}", Done: s.Status == SocialStatus.Closed));
        }));

        // In the day: timed entries in time order, then the rest by horse.
        static TimeSpan At(CalendarEntry x) => DateTime.TryParse(x.Time, CultureInfo.GetCultureInfo("en-AU"), DateTimeStyles.None, out var t) ? t.TimeOfDay : TimeSpan.MaxValue;
        return Results.Ok(entries.OrderBy(x => x.Date).ThenBy(At).ThenBy(x => x.Horse).ToList());
    }
}
