using System.Text;

namespace Paddockside.Domain;

/// <summary>One placement the matcher considered, with its score and the reasons in words (data-model.md MATCH_CANDIDATE).</summary>
public sealed record MatchSuggestion(Guid HorseId, Guid? EventId, int Score, IReadOnlyList<string> Reasons);

/// <summary>What the matcher knows about one horse: its names, pedigree, owners and open events.</summary>
/// <param name="LastActivity">The latest item on each open event, for "recent activity" and "quiet".</param>
public sealed record MatchHorse(Horse Horse, IReadOnlyList<Event> OpenEvents, IReadOnlyDictionary<Guid, DateTimeOffset> LastActivity);

/// <summary>
/// Tier 3 (data-model.md §3; messaging-channels.md §3.1, §3.3): mail that reached a general address, scored against
/// every horse by what it says and who sent it. A clear winner is Suggested for one-click confirmation; it is never
/// placed without a person. The reasons are written for staff ("Faultless Miss named in the subject").
/// </summary>
public static class InboundMatcher
{
    /// <summary>At least this score, and this far ahead of the next, to be Suggested rather than left Pending.</summary>
    public const int SuggestAt = 55;

    public const int ClearLead = 20;

    public static IReadOnlyList<MatchSuggestion> Score(string? subject, string body, string? localPart, Guid? senderPartyId,
        IReadOnlyList<MatchHorse> horses, DateTimeOffset receivedAt)
    {
        var inSubject = Normalise(subject);
        var inBody = Normalise(body);
        var hint = Normalise(localPart);
        var senderHorses = senderPartyId is { } sender ? horses.Where(h => OwnsNow(h.Horse, sender)).Select(h => h.Horse.Id).ToHashSet() : [];
        // A dam's name only identifies a horse when no other horse here shares it.
        var damCounts = horses.Where(h => h.Horse.Dam is not null).GroupBy(h => Normalise(h.Horse.Dam)).ToDictionary(g => g.Key, g => g.Count());

        var results = new List<MatchSuggestion>();
        foreach (var candidate in horses)
        {
            var horse = candidate.Horse;
            var reasons = new List<string>();
            var score = 0;
            void Count(int points, string reason)
            {
                score += points;
                reasons.Add(reason);
            }

            var names = horse.Names.Select(n => (n.Name, Key: Normalise(NameOnly(n.Name)))).Where(n => n.Key.Length >= 3).DistinctBy(n => n.Key).ToList();
            if (names.FirstOrDefault(n => Contains(inSubject, n.Key)).Name is { } inTitle) Count(40, $"{inTitle} named in the subject");
            else if (names.FirstOrDefault(n => Contains(inBody, n.Key)).Name is { } inText) Count(25, $"{inText} named in the message");

            if (hint.Length > 0 && names.FirstOrDefault(n => Contains(hint, n.Key)).Name is { } hinted)
                Count(35, $"Sent to {localPart}@, which looks like {hinted}");

            var damKey = Normalise(horse.Dam);
            if (damKey.Length >= 3 && damCounts.GetValueOrDefault(damKey) == 1 && (Contains(inSubject, damKey) || Contains(inBody, damKey)))
                Count(20, $"Dam {horse.Dam} mentioned");

            if (senderHorses.Contains(horse.Id))
            {
                if (senderHorses.Count == 1) Count(25, "Sent by an owner whose only horse here this is");
                else Count(10, "Sent by one of its owners");
            }

            if (score == 0) continue; // nothing ties this mail to this horse

            // Then the event: an open one in its window, preferring one the mail mentions and one that is busy.
            Event? bestEvent = null;
            var bestScore = 0;
            List<string> bestReasons = ["No open event in its window, so it would go on the horse"];
            var inWindow = candidate.OpenEvents.Where(e => e.IsInWindow(receivedAt)).ToList();
            foreach (var e in inWindow)
            {
                var eventReasons = new List<string> { inWindow.Count == 1 ? $"Its one open event in window: {e.Title}" : $"Open event in window: {e.Title}" };
                var eventScore = inWindow.Count == 1 ? 20 : 12;
                var words = Normalise(e.Title).Split(' ').Where(w => w.Length >= 4 && !char.IsDigit(w[0])).ToList();
                if (words.FirstOrDefault(w => Contains(inSubject, w) || Contains(inBody, w)) is { } mentioned)
                {
                    eventScore += 10;
                    eventReasons.Add($"\"{char.ToUpperInvariant(mentioned[0])}{mentioned[1..]}\" mentioned");
                }

                if (candidate.LastActivity.TryGetValue(e.Id, out var last))
                {
                    if (receivedAt - last <= TimeSpan.FromDays(7))
                    {
                        eventScore += 10;
                        eventReasons.Add("Recent activity on it");
                    }
                    else if (receivedAt - last > TimeSpan.FromDays(21))
                    {
                        eventScore -= 15;
                        eventReasons.Add("Quiet for three weeks");
                    }
                }

                if (bestEvent is null || eventScore > bestScore) (bestEvent, bestScore, bestReasons) = (e, eventScore, eventReasons);
            }

            results.Add(new MatchSuggestion(horse.Id, bestEvent?.Id, score + bestScore, [.. reasons, .. bestReasons]));
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>The suggestion to offer, if one is clearly ahead; otherwise the mail waits as Pending.</summary>
    public static MatchSuggestion? Winner(IReadOnlyList<MatchSuggestion> scored) =>
        scored is [var first, ..] && first.Score >= SuggestAt && (scored.Count == 1 || first.Score - scored[1].Score >= ClearLead) ? first : null;

    private static bool OwnsNow(Horse horse, Guid partyId) =>
        horse.CurrentManagementPeriod is { } period && horse.Interests.Any(i => i.PartyId == partyId && i.ManagementPeriodId == period.Id && i.IsActive);

    /// <summary>"Lot 231 (unnamed)" is matched as "Lot 231".</summary>
    private static string NameOnly(string name) => name.Replace("(unnamed)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    private static bool Contains(string text, string key) => key.Length > 0 && $" {text} ".Contains($" {key} ", StringComparison.Ordinal);

    /// <summary>Lower case, letters and digits only, single spaces: "Faultless Miss's" → "faultless miss s".</summary>
    private static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// A placement the matcher considered for one inbound message, kept with its score and reasons (data-model.md
/// MATCH_CANDIDATE). When staff place the message, the candidate they chose is flagged, so confirmations become
/// evidence for tuning the matcher instead of throwaway clicks.
/// </summary>
public sealed class MatchCandidate
{
    /// <summary>For EF Core materialisation.</summary>
    private MatchCandidate() => Reasons = [];

    public MatchCandidate(InboundMessage message, MatchSuggestion suggestion)
    {
        TenantId = message.TenantId;
        InboundMessageId = message.Id;
        HorseId = suggestion.HorseId;
        EventId = suggestion.EventId;
        Score = suggestion.Score;
        Reasons = suggestion.Reasons.ToList();
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid InboundMessageId { get; }

    public Guid HorseId { get; }

    public Guid? EventId { get; }

    public int Score { get; }

    public IReadOnlyList<string> Reasons { get; private set; }

    /// <summary>Staff placed the message here (on this horse, and this event when it had one).</summary>
    public bool Chosen { get; private set; }

    public void Choose() => Chosen = true;
}
