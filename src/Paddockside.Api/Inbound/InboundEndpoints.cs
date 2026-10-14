using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Inbound;

/// <summary>
/// What came in by email or text, by state (messaging-channels.md §3.2), and the pending queue (search-reporting.md
/// §3): Suggested and Pending mail, oldest first, each with the matcher's best guesses and their reasons, and the
/// actions to place it (confirm the suggestion, choose a horse and event, or leave it on the horse), ignore it, or
/// mark it as spam. Held (spam) is for tenant admins only (§3.4).
/// </summary>
public static class InboundEndpoints
{
    public sealed record AttachmentView(string Name, string? Dropped);

    /// <summary>A place the matcher considered, with how sure it was and why.</summary>
    public sealed record CandidateView(Guid HorseId, string Horse, Guid? EventId, string? Event, int Score, IReadOnlyList<string> Reasons);

    public sealed record InboundView(
        Guid Id,
        string State,
        string Received,
        string From,
        string To,
        string? Subject,
        string? Body,
        string? Reason,
        int? Tier,
        Guid? HorseId,
        string? Horse,
        Guid? EventId,
        string? Event,
        IReadOnlyList<AttachmentView> Attachments,
        string? Waiting = null,
        IReadOnlyList<CandidateView>? Candidates = null,
        string Channel = "Email");

    /// <param name="Waiting">Suggested plus Pending: the queue.</param>
    /// <param name="Ages">How long the queue has been waiting, in words ("2 under an hour · 1 older than a day").</param>
    /// <param name="MedianToPlace">The queue's health measure (search-reporting.md §3), over the last 30 days.</param>
    public sealed record InboundCounts(int Pending, int Placed, int Ignored, int? Held, int Waiting = 0, int Suggested = 0, string? Ages = null, string? MedianToPlace = null);

    /// <summary>Where staff can put mail: each managed horse and its open events.</summary>
    public sealed record PlaceOption(Guid HorseId, string Horse, IReadOnlyList<EventOption> Events);

    public sealed record EventOption(Guid Id, string Title, string KeyDate);

    /// <param name="EventId">Null leaves it on the horse, not on an event.</param>
    public sealed record PlaceRequest(Guid HorseId, Guid? EventId);

    private static readonly string[] CanPlace = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapInboundEndpoints(this IEndpointRouteBuilder app)
    {
        var inbound = app.MapGroup("/api/inbound").RequireAuthorization(StaffPolicy.Name);
        inbound.MapGet("/", List);
        inbound.MapGet("/counts", Counts);
        inbound.MapGet("/places", Places);
        inbound.MapPost("/{id:guid}/place", Place);
        inbound.MapPost("/{id:guid}/ignore", (Guid id, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            Dismiss(id, spam: false, user, db, audit, clock, ct));
        inbound.MapPost("/{id:guid}/spam", (Guid id, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            Dismiss(id, spam: true, user, db, audit, clock, ct));
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin);

    private static string StaffName(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? "staff";

    /// <summary>One state, newest first; or "Waiting" (the queue): Suggested and Pending together, oldest first.</summary>
    private static async Task<IResult> List(string? state, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var queue = string.Equals(state, "Waiting", StringComparison.OrdinalIgnoreCase);
        var wanted = queue ? InboundState.Pending : Enum.TryParse<InboundState>(state, ignoreCase: true, out var parsed) ? parsed : InboundState.Pending;
        if (wanted == InboundState.Held && !IsAdmin(user))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Held mail is for tenant admins only.");

        var query = db.InboundMessages.AsNoTracking()
            .Where(m => queue ? m.State == InboundState.Pending || m.State == InboundState.Suggested : m.State == wanted);
        var messages = await (queue ? query.OrderBy(m => m.ReceivedAt) : query.OrderByDescending(m => m.ReceivedAt)).Take(200).ToListAsync(cancellationToken);

        var messageIds = messages.Select(m => m.Id).ToList();
        var candidates = queue
            ? (await db.MatchCandidates.AsNoTracking().Where(c => messageIds.Contains(c.InboundMessageId)).ToListAsync(cancellationToken)).ToLookup(c => c.InboundMessageId)
            : null;
        var horseIds = messages.Where(m => m.HorseId != null).Select(m => m.HorseId!.Value)
            .Concat(candidates?.SelectMany(g => g).Select(c => c.HorseId) ?? []).Distinct().ToList();
        var horses = (await db.Horses.AsNoTracking().Where(h => horseIds.Contains(h.Id)).ToListAsync(cancellationToken)).ToDictionary(h => h.Id, h => h.Name);
        var eventIds = messages.Where(m => m.EventId != null).Select(m => m.EventId!.Value)
            .Concat(candidates?.SelectMany(g => g).Where(c => c.EventId != null).Select(c => c.EventId!.Value) ?? []).Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Title, cancellationToken);

        var now = clock.GetUtcNow();
        return Results.Ok(messages.Select(m => new InboundView(
            m.Id,
            m.State.ToString(),
            Words.DayAndTime(m.ReceivedAt, now),
            m.FromName is null ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>",
            m.RecipientAddress,
            m.Subject,
            m.DisplayBody ?? m.TextBody,
            m.Reason,
            m.MatchTier,
            m.HorseId,
            m.HorseId is { } h ? horses.GetValueOrDefault(h) : null,
            m.EventId,
            m.EventId is { } e ? events.GetValueOrDefault(e) : null,
            m.Attachments.Select(a => new AttachmentView(a.FileName, a.DroppedReason)).ToList(),
            queue ? $"Waiting {Duration(now - m.ReceivedAt)}" : null,
            candidates?[m.Id].OrderByDescending(c => c.Score)
                .Select(c => new CandidateView(c.HorseId, horses.GetValueOrDefault(c.HorseId) ?? "?", c.EventId, c.EventId is { } ce ? events.GetValueOrDefault(ce) : null, c.Score, c.Reasons))
                .ToList(),
            m.Channel)).ToList());
    }

    private static async Task<InboundCounts> Counts(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var counts = await db.InboundMessages.GroupBy(m => m.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Of(InboundState s) => counts.SingleOrDefault(c => c.Key == s)?.Count ?? 0;

        var now = clock.GetUtcNow();
        var waiting = await db.InboundMessages.Where(m => m.State == InboundState.Pending || m.State == InboundState.Suggested)
            .Select(m => m.ReceivedAt).ToListAsync(cancellationToken);
        var ages = new (string Label, int Count)[]
        {
            ("under an hour", waiting.Count(r => now - r < TimeSpan.FromHours(1))),
            ("today", waiting.Count(r => now - r >= TimeSpan.FromHours(1) && now - r < TimeSpan.FromDays(1))),
            ("older than a day", waiting.Count(r => now - r >= TimeSpan.FromDays(1))),
        }.Where(a => a.Count > 0).Select(a => $"{a.Count} {a.Label}").ToList();

        // Placed by a person from the queue (tiers 3 and 4) in the last 30 days: received to placed.
        var since = now.AddDays(-30);
        var placedFromQueue = await db.InboundMessages
            .Where(m => m.State == InboundState.Placed && m.MatchTier >= 3 && m.StateAt >= since)
            .Select(m => new { m.ReceivedAt, m.StateAt }).ToListAsync(cancellationToken);
        var durations = placedFromQueue.Select(p => p.StateAt - p.ReceivedAt).Order().ToList();
        var median = durations.Count == 0 ? (TimeSpan?)null : durations[durations.Count / 2];

        return new InboundCounts(Of(InboundState.Pending), Of(InboundState.Placed), Of(InboundState.Ignored), IsAdmin(user) ? Of(InboundState.Held) : null,
            waiting.Count, Of(InboundState.Suggested), ages.Count == 0 ? null : string.Join(" · ", ages),
            median is { } m ? $"Median time to place, last 30 days: {Duration(m)}" : null);
    }

    private static string Duration(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? "under a minute"
        : t < TimeSpan.FromHours(1) ? $"{(int)t.TotalMinutes} min"
        : t < TimeSpan.FromDays(1) ? $"{t.TotalHours:0.#} h"
        : $"{t.TotalDays:0.#} days";

    /// <summary>Managed horses and their open events, newest key date first: where mail can be placed.</summary>
    private static async Task<IReadOnlyList<PlaceOption>> Places(PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var horses = (await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).ToListAsync(cancellationToken))
            .Where(h => h.CurrentManagementPeriod is not null).ToList();
        var ids = horses.Select(h => h.Id).ToList();
        var events = (await db.Events.AsNoTracking().Where(e => ids.Contains(e.HorseId) && e.Status == EventStatus.Open).ToListAsync(cancellationToken))
            .ToLookup(e => e.HorseId);
        var now = clock.GetUtcNow();
        return horses.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Select(h => new PlaceOption(h.Id, h.Name, events[h.Id].OrderByDescending(e => e.KeyDate ?? e.OpenedAt)
                .Select(e => new EventOption(e.Id, e.Title, e.KeyDate is { } k ? Words.Day(k, now) : "No date")).ToList()))
            .ToList();
    }

    /// <summary>
    /// Places a waiting message: on the suggestion (one-click confirm) or wherever staff choose, on an event or on the
    /// horse. It becomes a staff-only item there, like any inbound mail; the candidate staff picked is flagged.
    /// </summary>
    private static async Task<IResult> Place(Guid id, PlaceRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanPlace.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read the queue but not place mail.");
        var message = await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is null) return Results.NotFound();
        if (!message.IsWaiting) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Someone has already dealt with this one.");

        var horse = await db.Horses.SingleOrDefaultAsync(h => h.Id == request.HorseId, cancellationToken);
        if (horse is null) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose one of our horses.");
        Event? e = null;
        if (request.EventId is { } eventId)
        {
            e = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId && x.HorseId == horse.Id, cancellationToken);
            if (e is null) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"That event isn't on {horse.Name}.");
        }

        var now = clock.GetUtcNow();
        var sender = message.PartyId is { } partyId ? await db.Parties.SingleOrDefaultAsync(p => p.Id == partyId, cancellationToken) : null;
        var confirmed = message.State == InboundState.Suggested && message.HorseId == horse.Id && message.EventId == e?.Id;
        var display = message.DisplayBody ?? message.TextBody ?? string.Empty;
        var item = StreamItem.Inbound(horse, e, message.Subject, display, sender?.DisplayName ?? message.FromName ?? message.FromAddress, sender?.Id,
            message.Channel == "Sms" ? "SMS" : "Email", message.ReceivedAt);
        db.StreamItems.Add(item);
        e?.RecordActivity();

        var where = e is null ? $"{horse.Name} (on the horse)" : $"{horse.Name}, {e.Title}";
        message.Place(confirmed ? 3 : 4, item, null, sender?.Id, display,
            confirmed ? $"Suggestion confirmed by {StaffName(user)}: {where}." : $"Placed by {StaffName(user)}: {where}.", now);
        foreach (var candidate in await db.MatchCandidates.Where(c => c.InboundMessageId == id && c.HorseId == horse.Id).ToListAsync(cancellationToken))
            if (candidate.EventId == e?.Id || e is null) candidate.Choose();

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(message.TenantId, "inbound.placed", $"Placed \"{message.Subject ?? "(no subject)"}\" from {message.FromAddress} on {where}",
            "InboundMessage", message.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Not for any horse: ignored (kept, never placed), or spam (held for tenant admins, as the provider's verdicts are).</summary>
    private static async Task<IResult> Dismiss(Guid id, bool spam, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanPlace.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read the queue but not change it.");
        var message = await db.InboundMessages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is null) return Results.NotFound();
        if (!message.IsWaiting) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Someone has already dealt with this one.");

        var now = clock.GetUtcNow();
        if (spam) message.Hold($"Marked as spam by {StaffName(user)}.", now);
        else message.Ignore($"Ignored by {StaffName(user)}.", now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(message.TenantId, spam ? "inbound.spam" : "inbound.ignored",
            $"{(spam ? "Marked as spam" : "Ignored")}: \"{message.Subject ?? "(no subject)"}\" from {message.FromAddress}", "InboundMessage", message.Id,
            cancellationToken: cancellationToken);
        return Results.NoContent();
    }
}
