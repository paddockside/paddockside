using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Inbound;

/// <summary>
/// What came in by email, by state (messaging-channels.md §3.2). Pending is the queue staff work through; Held
/// (spam) is for tenant admins only (§3.4).
/// </summary>
public static class InboundEndpoints
{
    public sealed record AttachmentView(string Name, string? Dropped);

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
        IReadOnlyList<AttachmentView> Attachments);

    public sealed record InboundCounts(int Pending, int Placed, int Ignored, int? Held);

    public static void MapInboundEndpoints(this IEndpointRouteBuilder app)
    {
        var inbound = app.MapGroup("/api/inbound").RequireAuthorization(StaffPolicy.Name);
        inbound.MapGet("/", List);
        inbound.MapGet("/counts", Counts);
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin);

    private static async Task<IResult> List(string? state, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var wanted = Enum.TryParse<InboundState>(state, ignoreCase: true, out var parsed) ? parsed : InboundState.Pending;
        if (wanted == InboundState.Held && !IsAdmin(user))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Held mail is for tenant admins only.");

        var messages = await db.InboundMessages.AsNoTracking()
            .Where(m => m.State == wanted)
            .OrderByDescending(m => m.ReceivedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var horseIds = messages.Where(m => m.HorseId != null).Select(m => m.HorseId!.Value).Distinct().ToList();
        var horses = (await db.Horses.AsNoTracking().Where(h => horseIds.Contains(h.Id)).ToListAsync(cancellationToken)).ToDictionary(h => h.Id, h => h.Name);
        var eventIds = messages.Where(m => m.EventId != null).Select(m => m.EventId!.Value).Distinct().ToList();
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
            m.Attachments.Select(a => new AttachmentView(a.FileName, a.DroppedReason)).ToList())).ToList());
    }

    private static async Task<InboundCounts> Counts(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var counts = await db.InboundMessages.GroupBy(m => m.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Of(InboundState s) => counts.SingleOrDefault(c => c.Key == s)?.Count ?? 0;
        return new InboundCounts(Of(InboundState.Pending), Of(InboundState.Placed), Of(InboundState.Ignored), IsAdmin(user) ? Of(InboundState.Held) : null);
    }
}
