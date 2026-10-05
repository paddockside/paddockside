using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Events;

/// <summary>
/// The event page (search-reporting.md §3, event stream): one event's items, newest first, and writing to it.
/// Staff only; the owner view arrives with the owner portal.
/// </summary>
public static class EventEndpoints
{
    public sealed record FieldView(string Label, string Value, string? Was);

    public sealed record ReplyView(string Author, string Channel, string At, string Body);

    public sealed record RecipientView(string Name, string Channel, string Status);

    public sealed record ItemView(
        Guid Id,
        string Kind,
        string Scope,
        string At,
        string? Title,
        string Body,
        string? Author,
        string? Step,
        string? Source,
        IReadOnlyList<FieldView> Fields,
        string? Correction,
        string? Audience,
        IReadOnlyList<ReplyView> Replies,
        IReadOnlyList<RecipientView> Recipients);

    public sealed record OwnerOption(Guid Id, string Name);

    public sealed record EventPage(
        HorseEndpoints.EventSummary Event,
        Guid HorseId,
        string HorseName,
        IReadOnlyList<ItemView> Items,
        IReadOnlyList<string> StepOptions,
        IReadOnlyList<OwnerOption> Owners);

    /// <param name="Audience">Owners, Trainer or InternalNote: one class only (D12).</param>
    public sealed record ComposeRequest(string Audience, string Scope, string? Step, string Channel, IReadOnlyList<Guid>? NamedParties, string Body);

    public sealed record Posted(Guid Id, int Recipients);

    private static readonly string[] CanPost = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events").RequireAuthorization(StaffPolicy.Name);
        events.MapGet("/{id:guid}", GetEvent);
        events.MapPost("/{id:guid}/items", Post);
    }

    private static async Task<IResult> GetEvent(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var e = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();

        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var items = await db.StreamItems.AsNoTracking().Where(i => i.EventId == id).ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => itemIds.Contains(d.StreamItemId)).ToListAsync(cancellationToken);
        var partyIds = deliveries.Select(d => d.PartyId).Concat(horse.Interests.Select(i => i.PartyId)).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);

        var now = clock.GetUtcNow();
        var type = EventTypes.Find(e.EventType);
        string? StepLabel(string? code) => code is null ? null : type.Steps.FirstOrDefault(s => s.Code == code)?.Label ?? code;

        var byId = items.ToDictionary(i => i.Id);
        var superseded = items.Where(i => i.SupersedesId is not null).Select(i => i.SupersedesId!.Value).ToHashSet();
        var replies = items.Where(i => i.InReplyToId is not null).ToLookup(i => i.InReplyToId!.Value);

        var views = items
            .Where(i => i.InReplyToId is null && !superseded.Contains(i.Id))
            .OrderByDescending(i => i.OccurredAt)
            .Select(i =>
            {
                var fields = i.Fields.Select(f => new FieldView(f.Label, f.Value, WasValue(i, f, byId))).ToList();
                var itemDeliveries = deliveries.Where(d => d.StreamItemId == i.Id).ToList();
                return new ItemView(
                    i.Id,
                    i.Kind.ToString(),
                    i.Scope.ToString(),
                    Words.DayAndTime(i.OccurredAt, now),
                    i.Title,
                    i.Body,
                    i.AuthorName,
                    StepLabel(i.StepCode),
                    i.Source,
                    fields,
                    i.SupersedesId is null ? null
                        : $"Corrected by {i.Source} on {Words.DayAndTime(i.OccurredAt, now)}{(i.CorrectionNote is null ? "" : $" ({i.CorrectionNote})")}",
                    i.Kind == StreamItemKind.Message && i.Direction == StreamItemDirection.Outbound
                        ? $"{Audience(i.Scope)} ({itemDeliveries.Count})"
                        : null,
                    replies[i.Id].OrderBy(r => r.OccurredAt)
                        .Select(r => new ReplyView(r.AuthorName ?? "Unknown sender", r.Channel ?? "email", Words.Recently(r.OccurredAt, now), r.Body))
                        .ToList(),
                    itemDeliveries
                        .OrderBy(d => parties.GetValueOrDefault(d.PartyId))
                        .Select(d => new RecipientView(parties.GetValueOrDefault(d.PartyId) ?? "Unknown", Channel(d.Channel), d.Status.ToString()))
                        .ToList());
            })
            .ToList();

        var period = horse.CurrentManagementPeriod;
        var owners = period is null ? [] : horse.Interests
            .Where(i => i.ManagementPeriodId == period.Id && i.IsActive)
            .Select(i => i.PartyId).Distinct()
            .Select(pid => new OwnerOption(pid, parties.GetValueOrDefault(pid) ?? "Unknown"))
            .OrderBy(o => o.Name)
            .ToList();

        var summary = HorseEndpoints.Summarise(e, items.Select(i => i.StepCode), items.Count == 0 ? null : items.Max(i => i.OccurredAt), now);
        return Results.Ok(new EventPage(summary, horse.Id, horse.Name, views, type.Steps.Select(s => s.Label).ToList(), owners));
    }

    private static async Task<IResult> Post(
        Guid id,
        ComposeRequest request,
        ClaimsPrincipal user,
        PaddocksideDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not post to them.");

        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Body)) return Problem("Write something first.");

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var type = EventTypes.Find(e.EventType);
        string? stepCode = null;
        if (!string.IsNullOrWhiteSpace(request.Step))
        {
            stepCode = type.Steps.FirstOrDefault(s => s.Label == request.Step || s.Code == request.Step)?.Code;
            if (stepCode is null) return Problem("That step is not part of this event.");
        }

        var now = clock.GetUtcNow();
        var author = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "Staff";
        if (!Enum.TryParse<StreamItemScope>(request.Scope, out var scope)) return Problem("Choose who can see it.");

        StreamItem item;
        IReadOnlyList<Delivery> deliveries = [];
        switch (request.Audience)
        {
            case "InternalNote":
                if (scope != StreamItemScope.Internal) return Problem("A staff note is internal.");
                item = StreamItem.Note(horse, e, request.Body.Trim(), now, author, stepCode);
                break;

            case "Owners":
                if (scope is not (StreamItemScope.Owners or StreamItemScope.OwnersAtTheTime or StreamItemScope.NamedParties))
                    return Problem("A message to owners must be visible to owners.");
                var named = request.NamedParties ?? [];
                if (scope == StreamItemScope.NamedParties)
                {
                    var current = horse.CurrentManagementPeriod is { } period
                        ? horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.PartyId).ToHashSet()
                        : [];
                    if (named.Count == 0 || !named.All(current.Contains)) return Problem("Choose at least one current owner.");
                }

                item = StreamItem.Message(horse, e, null, request.Body.Trim(), scope, now, author, scope == StreamItemScope.NamedParties ? named : null, stepCode);
                deliveries = Delivery.ForOwners(tenant, horse, item, ToChannel(request.Channel), now);
                break;

            case "Trainer":
                if (scope != StreamItemScope.Trainer) return Problem("A message to the trainer is scoped to the trainer.");
                // The trainer becomes a recipient once party roles exist; until then it is recorded, not sent.
                item = StreamItem.Message(horse, e, null, request.Body.Trim(), scope, now, author, stepCode: stepCode);
                break;

            default:
                return Problem("Choose who to send it to.");
        }

        e.RecordActivity();
        db.StreamItems.Add(item);
        db.Deliveries.AddRange(deliveries);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/events/{id}", new Posted(item.Id, deliveries.Count));
    }

    /// <summary>For a corrected fact, the value it had before, when it changed.</summary>
    private static string? WasValue(StreamItem item, FactField field, IReadOnlyDictionary<Guid, StreamItem> byId)
    {
        if (item.SupersedesId is not { } previousId || !byId.TryGetValue(previousId, out var previous)) return null;
        var before = previous.Fields.FirstOrDefault(f => f.Label == field.Label)?.Value;
        return before is not null && before != field.Value ? before : null;
    }

    private static string Audience(StreamItemScope scope) => scope switch
    {
        StreamItemScope.Owners => "Owners",
        StreamItemScope.OwnersAtTheTime => "Owners at the time",
        StreamItemScope.NamedParties => "Named owners",
        StreamItemScope.Trainer => "Trainer",
        _ => "Staff",
    };

    private static string Channel(DeliveryChannel channel) => channel switch
    {
        DeliveryChannel.Sms => "Text",
        DeliveryChannel.Portal => "Portal",
        _ => "Email",
    };

    private static DeliveryChannel ToChannel(string channel) => channel switch
    {
        "Sms" => DeliveryChannel.Sms,
        "PortalOnly" => DeliveryChannel.Portal,
        _ => DeliveryChannel.Email, // "Preferred" uses email until notification preferences exist
    };

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
