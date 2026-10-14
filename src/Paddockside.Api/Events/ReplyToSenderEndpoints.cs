using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Events;

/// <summary>
/// Replying to someone who wrote in (a trainer, an agent, anyone who emailed the office): a new message on the same
/// horse and event, to them alone, "Re:" their subject (messaging-channels.md §3.5: a reply is a new outbound message
/// with its own reply address, so their answer threads back under it). Someone not yet in the contacts is added as
/// one: a name and an email, no ownership and no portal access.
/// </summary>
public static class ReplyToSenderEndpoints
{
    public sealed record ReplyToSenderRequest(string Body);

    public sealed record Replied(Guid Id, string To);

    private static readonly string[] CanReply = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapReplyToSenderEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/items/{id:guid}/reply-to-sender", Reply).RequireAuthorization(StaffPolicy.Name);

    private static async Task<IResult> Reply(Guid id, ReplyToSenderRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanReply.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read mail but not reply to it.");
        if (string.IsNullOrWhiteSpace(request.Body)) return Problem("Write your reply first.");

        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item is not { Kind: StreamItemKind.Message, Direction: StreamItemDirection.Inbound, InReplyToId: null })
            return Problem("Only an email or text that came in can be answered this way.");

        // Who wrote it: the contact it was matched to, else the address it came from.
        var party = item.AuthorPartyId is { } authorId ? await db.Parties.SingleOrDefaultAsync(p => p.Id == authorId, cancellationToken) : null;
        if (party?.PrimaryEmail is null)
        {
            var inbound = await db.InboundMessages.AsNoTracking().FirstOrDefaultAsync(m => m.StreamItemId == id && m.Channel == "Email", cancellationToken);
            if (inbound is null) return Problem("We don't have an email address to reply to.");
            var address = inbound.FromAddress;
            party = (await db.Parties.Where(p => p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == address)).ToListAsync(cancellationToken)).FirstOrDefault();
            if (party is null)
            {
                party = new Party(item.TenantId, inbound.FromName ?? address);
                party.AddEmail(address);
                db.Parties.Add(party);
            }
        }

        var horse = await db.Horses.SingleAsync(h => h.Id == item.HorseId, cancellationToken);
        var e = item.EventId is { } eventId ? await db.Events.SingleAsync(x => x.Id == eventId, cancellationToken) : null;
        var now = clock.GetUtcNow();
        var author = user.FindFirstValue(ClaimTypes.Email) ?? "Staff";
        var subject = item.Title is { Length: > 0 } title
            ? title.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? title : $"Re: {title}"
            : $"Re: {horse.Name}";

        var reply = StreamItem.Message(horse, e, subject, request.Body.Trim(), StreamItemScope.NamedParties, now, author, [party.Id]);
        db.StreamItems.Add(reply);
        await OutboundQueue.QueueDirectAsync(db, reply, party, now, cancellationToken);
        e?.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);

        var to = party.PrimaryEmail!.Value;
        await audit.RecordAsync(item.TenantId, "message.replied", $"Replied to {party.DisplayName} <{to}> about {horse.Name}: \"{subject}\"", "StreamItem", reply.Id,
            cancellationToken: cancellationToken);
        return Results.Created($"/api/items/{reply.Id}", new Replied(reply.Id, $"{party.DisplayName} <{to}>"));
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
