using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Persistence;

/// <summary>
/// Records answers to a message's question, however they arrive: a tap in the portal, words in an email or text
/// reply, or staff taking it down from a phone call. The latest answer replaces the last, so an owner can change
/// their mind. Saving is the caller's.
/// </summary>
public static class PromptAnswering
{
    /// <summary>Sets <paramref name="partyId"/>'s answer; returns whether it changed (false when it was already that).</summary>
    public static async Task<(PromptAnswer Answer, bool Changed)> RecordAsync(PaddocksideDbContext db, StreamItem message, Guid partyId, string value,
        AnswerSource source, string recordedBy, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var existing = await db.PromptAnswers.SingleOrDefaultAsync(a => a.StreamItemId == message.Id && a.PartyId == partyId, cancellationToken)
                       ?? db.PromptAnswers.Local.SingleOrDefault(a => a.StreamItemId == message.Id && a.PartyId == partyId);
        if (existing is null)
        {
            var answer = new PromptAnswer(message, partyId, value, source, recordedBy, at);
            db.PromptAnswers.Add(answer);
            return (answer, true);
        }

        var changed = !string.Equals(existing.Value, message.Prompt?.Normalise(value), StringComparison.Ordinal);
        existing.Change(message, value, source, recordedBy, at);
        return (existing, changed);
    }

    /// <summary>
    /// A reply in words to a message with a question: if the words give a clear answer ("Two please"), it is
    /// recorded as read from the reply. Ambiguous words record nothing; staff read the reply and set it.
    /// </summary>
    public static async Task<PromptAnswer?> ReadFromReplyAsync(PaddocksideDbContext db, StreamItem message, Party party, string reply, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (message.Prompt?.ReadFrom(reply) is not { } value) return null;
        return (await RecordAsync(db, message, party.Id, value, AnswerSource.ReadFromReply, party.DisplayName, at, cancellationToken)).Answer;
    }
}
