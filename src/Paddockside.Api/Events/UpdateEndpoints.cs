using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Account;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Media;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Events;

/// <summary>
/// Sending an update from a phone (David, 25 Nov 2026): from the horse, on its most likely event or its standing "General
/// updates", with words, voice, video and photos in any mix. Files come up in pieces while the update is written
/// (<see cref="MediaUpload"/>); the send names them, carries a key so it can be repeated safely after a dropped signal, and
/// follows the same rules as the event page's compose box.
/// </summary>
public static class UpdateEndpoints
{
    /// <param name="Id">Null for "General updates" on a horse that hasn't had one yet: sending starts it.</param>
    public sealed record UpdateEvent(Guid? Id, string Title, string When, IReadOnlyList<string> Steps, IReadOnlyDictionary<string, EventEndpoints.PromptRequest> StepPrompts);

    public sealed record UpdateOwner(Guid Id, string Name);

    /// <param name="Events">The likeliest first; "General updates" is always one of them.</param>
    public sealed record UpdateOptions(string Horse, bool Managed, IReadOnlyList<UpdateEvent> Events, IReadOnlyList<UpdateOwner> Owners);

    /// <param name="EventId">Null for General updates.</param>
    /// <param name="Scope">Owners, NamedParties or Internal (staff only).</param>
    public sealed record UpdateRequest(Guid Key, Guid? EventId, string Scope, string? Step, string? Channel, IReadOnlyList<Guid>? NamedParties, string? Body,
        EventEndpoints.PromptRequest? Prompt, IReadOnlyList<Guid>? UploadIds);

    public sealed record UpdateRecipients(Guid? EventId, string Scope, string? Step, string? Channel, IReadOnlyList<Guid>? NamedParties, bool Media);

    public sealed record Sent(Guid ItemId, Guid EventId, int Recipients);

    /// <summary>How near an event's date has to be for it to be what an update is most likely about.</summary>
    private const int LikelyDays = 14;

    private static readonly string[] CanPost = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var horses = app.MapGroup("/api/horses/{id:guid}/updates").RequireAuthorization(StaffPolicy.Name);
        horses.MapGet("/options", Options);
        horses.MapPost("/recipients", Recipients);
        horses.MapPost("/", Send);
    }

    private static async Task<IResult> Options(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        var now = clock.GetUtcNow();
        var open = await db.Events.AsNoTracking().Where(e => e.HorseId == id && e.Status == EventStatus.Open).ToListAsync(cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var ownerIds = horse.CurrentManagementPeriod is { } period
            ? horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive && i.PartyId != tenant.HousePartyId).Select(i => i.PartyId).Distinct().ToList()
            : [];
        var owners = await db.Parties.AsNoTracking().Where(p => ownerIds.Contains(p.Id)).OrderBy(p => p.DisplayName).Select(p => new UpdateOwner(p.Id, p.DisplayName)).ToListAsync(cancellationToken);

        double Away(DomainEvent e) => e.KeyDate is { } key ? Math.Abs((Words.Local(key).Date - Words.Local(now).Date).TotalDays) : double.MaxValue;
        var dated = open.Where(e => !e.Standing).OrderBy(Away).ThenByDescending(e => e.OpenedAt).ToList();
        var standing = open.FirstOrDefault(e => e.Standing);
        var general = new UpdateEvent(standing?.Id, DomainEvent.StandingTitle, "Anything not about one event", [], new Dictionary<string, EventEndpoints.PromptRequest>());
        var likely = dated.Where(e => Away(e) <= LikelyDays).Select(ToOption).ToList();
        var others = dated.Where(e => Away(e) > LikelyDays).Select(ToOption).ToList();
        return Results.Ok(new UpdateOptions(horse.Name, horse.CurrentManagementPeriod is not null, [.. likely, general, .. others], owners));

        UpdateEvent ToOption(DomainEvent e)
        {
            var type = EventTypes.Find(e.EventType);
            return new UpdateEvent(e.Id, e.Title, e.KeyDate is { } key ? Words.Day(key, now) : type.Name, type.Steps.Select(s => s.Label).ToList(),
                type.Steps.Where(s => s.Prompt is not null).ToDictionary(s => s.Label, s => EventEndpoints.ToRequest(s.Prompt!)));
        }
    }

    private static async Task<IResult> Recipients(Guid id, UpdateRecipients request, PaddocksideDbContext db, TimeProvider clock, ISmsSender texts, CancellationToken cancellationToken)
    {
        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        var e = request.EventId is { } eventId
            ? await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == eventId && x.HorseId == id, cancellationToken)
            : await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.HorseId == id && x.Standing && x.Status == EventStatus.Open, cancellationToken)
                ?? (horse.CurrentManagementPeriod is null ? null : DomainEvent.GeneralUpdates(horse, clock.GetUtcNow())); // as it would be, never saved
        if (e is null) return horse.CurrentManagementPeriod is null ? Problem($"{horse.Name} is no longer managed, so its updates have ended.") : Results.NotFound();
        return await EventEndpoints.RecipientsForAsync(horse, e, new EventEndpoints.RecipientsRequest(request.Scope, request.Step, request.Channel ?? "Preferred", request.NamedParties, request.Media),
            db, clock.GetUtcNow(), texts, cancellationToken);
    }

    private static async Task<IResult> Send(Guid id, UpdateRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read updates but not send them.");
        if (request.Key == Guid.Empty || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var person)) return Problem("Open the update again and send it from there.");
        if (await AlreadyAsync(db, request.Key, cancellationToken) is { } before) return before;

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        if (horse.CurrentManagementPeriod is null) return Problem($"{horse.Name} is no longer managed, so its updates have ended.");
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var uploadIds = (request.UploadIds ?? []).Distinct().ToList();
        if (uploadIds.Count > MediaEndpoints.MaxFiles) return Problem($"Add up to {MediaEndpoints.MaxFiles} at a time.");
        var uploads = await db.MediaUploads.Where(u => uploadIds.Contains(u.Id) && u.PersonId == person).ToListAsync(cancellationToken);
        if (uploads.Count != uploadIds.Count) return Problem("One of the files didn't arrive. Take it off and add it again.");
        if (uploads.Any(u => u.CompletedAt is null)) return Problem("One of the files hasn't finished uploading.");
        if (uploads.Count > 0 && request.Prompt is not null) return Problem("Send the question on its own, then the photos, video or voice note.");

        DomainEvent? e;
        if (request.EventId is { } eventId)
        {
            e = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId && x.HorseId == id, cancellationToken);
            if (e is null) return Results.NotFound();
        }
        else
        {
            e = await GeneralUpdatesAsync(db, horse, now, cancellationToken);
        }

        var author = await StaffNames.AuthorAsync(user, db, cancellationToken);
        StreamItem item;
        IReadOnlyList<Delivery> deliveries;
        if (uploads.Count == 0)
        {
            var audience = request.Scope == nameof(StreamItemScope.Internal) ? "InternalNote" : "Owners";
            if (audience == "Owners" && request.Scope is not (nameof(StreamItemScope.Owners) or nameof(StreamItemScope.NamedParties))) return Problem("Choose who can see it.");
            var posted = await EventEndpoints.PrepareAsync(e, horse, tenant, new EventEndpoints.ComposeRequest(audience, request.Scope, request.Step, request.Channel ?? "Preferred",
                request.NamedParties, request.Body ?? "", audience == "Owners" ? request.Prompt : null), author, db, texts, now, cancellationToken);
            if (posted.Problem is { } problem) return problem;
            (item, deliveries) = (posted.Item!, posted.Deliveries);
        }
        else
        {
            var made = await MediaAsync(e, horse, tenant, request, uploads.OrderBy(u => uploadIds.IndexOf(u.Id)).ToList(), author, db, texts, now, cancellationToken);
            if (made.Problem is { } problem) return problem;
            (item, deliveries) = (made.Item!, made.Deliveries);
        }

        e.RecordActivity();
        db.SentUpdates.Add(new SentUpdate(item, request.Key, person, deliveries.Count, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            // Sent twice at once (a retry crossing the first): the one that got there is the update.
            db.ChangeTracker.Clear();
            if (await AlreadyAsync(db, request.Key, cancellationToken) is { } first) return first;
            throw;
        }

        if (deliveries.Count > 0) emailSignal.Notify();
        await audit.RecordAsync(tenant.Id, "update.sent",
            $"{(uploads.Count == 0 ? "Wrote" : $"Added {MediaEndpoints.Describe(uploads.Select(u => MediaAsset.Types[u.ContentType].Kind).ToList())}")} on \"{e.Title}\" for {horse.Name} ({(item.Scope == StreamItemScope.Internal ? "staff only" : "for owners")})",
            "StreamItem", item.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/events/{e.Id}", new Sent(item.Id, e.Id, deliveries.Count));
    }

    private static async Task<(IResult? Problem, StreamItem? Item, IReadOnlyList<Delivery> Deliveries)> MediaAsync(DomainEvent e, Horse horse, Tenant tenant, UpdateRequest request,
        IReadOnlyList<MediaUpload> uploads, string author, PaddocksideDbContext db, ISmsSender texts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<StreamItemScope>(request.Scope, out var scope) || scope is not (StreamItemScope.Owners or StreamItemScope.NamedParties or StreamItemScope.Internal))
            return (Problem("Choose who can see it."), null, []);
        if (e.Status == EventStatus.Draft && scope != StreamItemScope.Internal)
            return (Problem("This event is still a draft: owners can't see it. Open it first, or keep these for staff."), null, []);
        if ((request.Body?.Length ?? 0) > 4000) return (Problem("That's too long to go with photos or video. Keep it under 4,000 characters."), null, []);
        var named = request.NamedParties ?? [];
        if (scope == StreamItemScope.NamedParties)
        {
            var current = horse.Interests.Where(i => i.ManagementPeriodId == horse.CurrentManagementPeriod!.Id && i.IsActive).Select(i => i.PartyId).ToHashSet();
            if (named.Count == 0 || !named.All(current.Contains)) return (Problem("Choose at least one current owner."), null, []);
        }

        string? stepCode = null;
        if (!string.IsNullOrWhiteSpace(request.Step))
        {
            stepCode = EventTypes.Find(e.EventType).Steps.FirstOrDefault(s => s.Label == request.Step || s.Code == request.Step)?.Code;
            if (stepCode is null) return (Problem("That step is not part of this event."), null, []);
        }

        try
        {
            var kinds = uploads.Select(u => MediaAsset.Types[u.ContentType].Kind).ToList();
            var item = StreamItem.Media(horse, e, MediaEndpoints.Title(kinds), request.Body, scope, now, author, scope == StreamItemScope.NamedParties ? named : null, stepCode);
            foreach (var upload in uploads) upload.UseOn(item);
            db.StreamItems.Add(item);
            // A millisecond apart, so they keep the order they were added in.
            db.MediaAssets.AddRange(uploads.Select((u, n) => new MediaAsset(item, u, now.AddMilliseconds(n))));
            IReadOnlyList<Delivery> deliveries = scope == StreamItemScope.Internal ? []
                : await OutboundQueue.QueueAsync(db, tenant, horse, e, item, EventEndpoints.ToChannel(request.Channel ?? "Preferred"), texts.IsConfigured, now, cancellationToken);
            return (null, item, deliveries);
        }
        catch (DomainException ex)
        {
            return (Problem(ex.Message), null, []);
        }
    }

    /// <summary>The horse's standing "General updates", started if it hasn't one open; saved with the update.</summary>
    private static async Task<DomainEvent> GeneralUpdatesAsync(PaddocksideDbContext db, Horse horse, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.Events.Where(x => x.HorseId == horse.Id && x.Standing && x.Status == EventStatus.Open).OrderBy(x => x.OpenedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;
        var started = DomainEvent.GeneralUpdates(horse, now);
        db.Events.Add(started);
        return started;
    }

    private static async Task<IResult?> AlreadyAsync(PaddocksideDbContext db, Guid key, CancellationToken cancellationToken) =>
        await db.SentUpdates.AsNoTracking().Where(s => s.Key == key)
            .Join(db.StreamItems, s => s.StreamItemId, i => i.Id, (s, i) => new { s.StreamItemId, i.EventId, s.Recipients }).SingleOrDefaultAsync(cancellationToken) is { } sent
            ? Results.Ok(new Sent(sent.StreamItemId, sent.EventId!.Value, sent.Recipients))
            : null;

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
