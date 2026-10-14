using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Events;

/// <summary>
/// Staff creating events by hand and opening drafts (event-library.md §2, §5). An event starts Open, or as a Draft
/// that owners can't see and that sends nothing until someone opens it. One event per type per key date per horse:
/// a second race start on the same day is refused, not duplicated.
/// </summary>
public static class EventLifecycleEndpoints
{
    /// <param name="KeyDateLabel">What the key date means for this type, in staff words ("Race day").</param>
    public sealed record EventTypeOption(string Code, string Name, string KeyDateLabel, string TitleExample);

    /// <param name="KeyDate">A calendar date, yyyy-MM-dd, Sydney time; optional for types that allow it.</param>
    public sealed record NewEventRequest(string Type, string Title, string? KeyDate, bool Open);

    public sealed record Created(Guid Id);

    private static readonly string[] CanCreate = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    public static readonly IReadOnlyList<EventTypeOption> Types =
    [
        new(EventTypes.RaceStart.Code, EventTypes.RaceStart.Name, "Race day", "Randwick, Race 6"),
        new(EventTypes.BarrierTrial.Code, EventTypes.BarrierTrial.Name, "Trial day", "Rosehill trial, heat 4"),
        new(EventTypes.Veterinary.Code, EventTypes.Veterinary.Name, "Date of the visit", "Scope after trackwork"),
        new(EventTypes.General.Code, EventTypes.General.Name, "Date", "Arrival at the spelling farm"),
    ];

    public static void MapEventLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/event-types", () => Results.Ok(Types)).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/events", Create).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/events/{id:guid}/open", Open).RequireAuthorization(StaffPolicy.Name);
    }

    private static async Task<IResult> Create(Guid id, NewEventRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanCreate.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not create them.");
        var horse = await db.Horses.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        if (Types.FirstOrDefault(t => t.Code == request.Type) is not { } type) return Problem("Choose what kind of event it is.");
        if (string.IsNullOrWhiteSpace(request.Title)) return Problem($"Give it a title, for example \"{type.TitleExample}\".");
        if (request.Title.Trim().Length > 200) return Problem("That title is too long; keep it under 200 characters.");

        DateTimeOffset? keyDate = null;
        if (!string.IsNullOrWhiteSpace(request.KeyDate))
        {
            if (!DateOnly.TryParseExact(request.KeyDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Problem("That date isn't one we can read.");
            var midnight = day.ToDateTime(TimeOnly.MinValue);
            keyDate = new DateTimeOffset(midnight, Sydney.GetUtcOffset(midnight));
        }
        else if (type.Code is "RaceStart" or "BarrierTrial")
        {
            return Problem($"A {type.Name.ToLowerInvariant()} needs its {type.KeyDateLabel.ToLowerInvariant()}.");
        }

        // One per type per key date on a horse (event-library.md §5): a second one is a mistake, not a second race.
        var sameDay = await db.Events.Where(e => e.HorseId == id && e.EventType == type.Code && e.Status != EventStatus.Cancelled)
            .ToListAsync(cancellationToken);
        if (keyDate is { } k && sameDay.FirstOrDefault(e => e.KeyDate is { } other && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(other, Sydney).DateTime) == DateOnly.FromDateTime(k.DateTime)) is { } existing)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"{horse.Name} already has a {type.Name.ToLowerInvariant()} on {Words.Day(k, clock.GetUtcNow())}: \"{existing.Title}\".",
                extensions: new Dictionary<string, object?> { ["eventId"] = existing.Id });

        var now = clock.GetUtcNow();
        var e = new DomainEvent(horse, type.Code, request.Title.Trim(), keyDate);
        if (request.Open) e.Open(now);
        db.Events.Add(e);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(horse.TenantId, "event.created",
            $"{(request.Open ? "Opened" : "Drafted")} {type.Name.ToLowerInvariant()} \"{e.Title}\" on {horse.Name}", "Event", e.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/events/{e.Id}", new Created(e.Id));
    }

    /// <summary>A draft becomes visible to owners and can be written to (P23: facts create drafts; only people open them).</summary>
    private static async Task<IResult> Open(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanCreate.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not open them.");
        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        if (e.Status != EventStatus.Draft) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This event is already open.");

        e.Open(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(e.TenantId, "event.opened", $"Opened \"{e.Title}\"", "Event", e.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
