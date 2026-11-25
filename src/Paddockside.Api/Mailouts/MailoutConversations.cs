using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Account;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Mailouts;

/// <summary>
/// What people write back to a mailout or a social event's invitation, and staff's answers (David, 25 Nov 2026): one
/// conversation per person, shown on the mailout's page and on the social event's. An answer is emailed to whoever wrote,
/// at the address they wrote from, and their next reply comes back to the same conversation. A reply waits, and is on
/// Today, until it's answered or marked as needing no answer.
/// </summary>
public static class MailoutConversations
{
    /// <param name="Out">From staff; otherwise from them.</param>
    /// <param name="Who">Whoever wrote it.</param>
    /// <param name="Delivery">Staff's: "Sending", "Sent", "Delivered", "Opened", "Bounced" or "Not sent".</param>
    /// <param name="Note">Why it didn't go, or who said a reply needs no answer.</param>
    /// <param name="Files">What came attached (names only).</param>
    public sealed record Message(Guid Id, bool Out, string Who, string At, string Body, string? Delivery = null, string? Note = null, IReadOnlyList<string>? Files = null);

    /// <param name="Address">Where an answer goes: the address they last wrote from.</param>
    /// <param name="Waiting">They wrote last, and it hasn't been answered or set aside.</param>
    public sealed record Conversation(Guid RecipientId, Guid PartyId, string Name, string Address, bool Waiting, IReadOnlyList<Message> Messages);

    public sealed record AnswerRequest(string? Body);

    public sealed record Answered(string To);

    private static readonly string[] CanAnswer = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapMailoutConversations(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/api/mailouts/conversations").RequireAuthorization(StaffPolicy.Name);
        conversations.MapPost("/{recipientId:guid}/answer", Answer);
        conversations.MapPost("/{recipientId:guid}/done", Done);
    }

    /// <summary>Every conversation on these mailouts, the ones waiting for an answer first, then the latest.</summary>
    internal static async Task<IReadOnlyList<Conversation>> ForAsync(PaddocksideDbContext db, IReadOnlyCollection<Guid> mailoutIds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var replies = await db.MailoutReplies.AsNoTracking().Where(r => mailoutIds.Contains(r.MailoutId)).ToListAsync(cancellationToken);
        if (replies.Count == 0) return [];
        var answers = (await db.MailoutAnswers.AsNoTracking().Where(a => mailoutIds.Contains(a.MailoutId)).ToListAsync(cancellationToken)).ToLookup(a => a.RecipientId);
        var partyIds = replies.Select(r => r.PartyId).Distinct().ToList();
        var names = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
        var inboundIds = replies.Select(r => r.InboundMessageId).ToList();
        var files = (await db.InboundMessages.AsNoTracking().Where(m => inboundIds.Contains(m.Id)).ToListAsync(cancellationToken))
            .ToDictionary(m => m.Id, m => m.Attachments.Where(a => a.DroppedReason is null).Select(a => a.FileName).ToList());

        return replies.GroupBy(r => r.RecipientId)
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.ReceivedAt)!;
                var name = names.GetValueOrDefault(latest.PartyId) ?? latest.FromAddress;
                var messages = g.Select(r => (At: r.ReceivedAt, Message: new Message(r.Id, false, name, Words.Recently(r.ReceivedAt, now), r.Body,
                        Note: r.SetAsideBy is { } by ? $"No answer needed ({by})" : null,
                        Files: files.GetValueOrDefault(r.InboundMessageId) is { Count: > 0 } f ? f : null)))
                    .Concat(answers[g.Key].Select(a => (At: a.CreatedAt, Message: new Message(a.Id, true, a.Author, Words.Recently(a.CreatedAt, now), a.Body, Delivery(a), a.Note))))
                    .OrderBy(x => x.At).Select(x => x.Message).ToList();
                return (latest.ReceivedAt, Conversation: new Conversation(g.Key, latest.PartyId, name, latest.FromAddress, g.Any(r => r.Waiting), messages));
            })
            .OrderByDescending(x => x.Conversation.Waiting).ThenByDescending(x => x.ReceivedAt)
            .Select(x => x.Conversation)
            .ToList();
    }

    private static string Delivery(MailoutAnswer a) => a.Status switch
    {
        MailoutRecipientStatus.Queued => "Sending",
        MailoutRecipientStatus.Skipped => "Not sent",
        var status => status.ToString(),
    };

    private static async Task<IResult> Answer(Guid recipientId, AnswerRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IEmailSender email, AuditLog audit,
        TimeProvider clock, EmailDispatchSignal emailSignal, CancellationToken cancellationToken)
    {
        if (!CanAnswer.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        if (!email.IsConfigured) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Email isn't set up here, so nothing can be sent.");
        var replies = await db.MailoutReplies.Where(r => r.RecipientId == recipientId).OrderBy(r => r.ReceivedAt).ToListAsync(cancellationToken);
        if (replies.Count == 0) return Results.NotFound();
        var recipient = await db.MailoutRecipients.SingleAsync(r => r.Id == recipientId, cancellationToken);
        var mailout = await db.Mailouts.SingleAsync(m => m.Id == recipient.MailoutId, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);

        var author = await StaffNames.AuthorAsync(user, db, cancellationToken);
        var saved = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var person)
            ? await db.StaffSignatures.AsNoTracking().SingleOrDefaultAsync(s => s.PersonId == person, cancellationToken)
            : null;
        var signature = saved is { Text.Length: > 0 } ? saved.Text : author.Contains('@') ? $"The team at {tenant.Name}" : $"{author}\n{tenant.Name}";

        var now = clock.GetUtcNow();
        MailoutAnswer answer;
        try
        {
            answer = new MailoutAnswer(recipient, replies[^1], request.Body ?? "", signature, author, now);
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }

        db.MailoutAnswers.Add(answer);
        foreach (var reply in replies.Where(r => r.Waiting)) reply.AnsweredAt(now);
        await db.SaveChangesAsync(cancellationToken);
        emailSignal.Notify();
        await audit.RecordAsync(tenant.Id, "mailout.answered", $"Answered {answer.ToAddress} about \"{mailout.Subject}\"", "Mailout", mailout.Id, cancellationToken: cancellationToken);
        return Results.Ok(new Answered(answer.ToAddress));
    }

    private static async Task<IResult> Done(Guid recipientId, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanAnswer.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var replies = await db.MailoutReplies.Where(r => r.RecipientId == recipientId).ToListAsync(cancellationToken);
        if (replies.Count == 0) return Results.NotFound();
        var by = await StaffNames.AuthorAsync(user, db, cancellationToken);
        foreach (var reply in replies.Where(r => r.Waiting)) reply.SetAside(by, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read replies but not answer them.");
}
