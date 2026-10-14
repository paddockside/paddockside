using System.Text.RegularExpressions;

namespace Paddockside.Domain;

public enum PromptKind
{
    /// <summary>A number from 0 to <see cref="MessagePrompt.Max"/>: "How many tickets?"</summary>
    Count,

    /// <summary>Yes or no: "Will you come to the presentation?"</summary>
    YesNo,

    /// <summary>One of <see cref="MessagePrompt.Options"/>: "Which share?"</summary>
    Choice,
}

/// <summary>
/// A question on a message with a structured answer (event-page-mockup-notes.md, spec item 1): owners answer with
/// big buttons, or in words by email or text, and staff see a tally. Stored with the message it belongs to.
/// </summary>
public sealed record MessagePrompt(PromptKind Kind, string Question, IReadOnlyList<string> Options, int Max)
{
    public const int LargestCount = 20;

    public static MessagePrompt Count(string question, int max = 10) => Create(PromptKind.Count, question, [], max);

    public static MessagePrompt YesNo(string question) => Create(PromptKind.YesNo, question, ["Yes", "No"], 0);

    public static MessagePrompt Choice(string question, IEnumerable<string> options) => Create(PromptKind.Choice, question, options.ToList(), 0);

    public static MessagePrompt Create(PromptKind kind, string question, IReadOnlyList<string> options, int max)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new DomainException("Write the question.");
        var cleaned = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return kind switch
        {
            PromptKind.Count when max is < 1 or > LargestCount => throw new DomainException($"The most someone can ask for is between 1 and {LargestCount}."),
            PromptKind.Count => new(kind, question.Trim(), [], max),
            PromptKind.YesNo => new(kind, question.Trim(), ["Yes", "No"], 0),
            PromptKind.Choice when cleaned.Count is < 2 or > 8 => throw new DomainException("Give between 2 and 8 choices."),
            PromptKind.Choice => new(kind, question.Trim(), cleaned, 0),
            _ => throw new DomainException("Unknown kind of question."),
        };
    }

    /// <summary>Every answer someone can give, as the buttons show them.</summary>
    public IReadOnlyList<string> Answers => Kind == PromptKind.Count ? Enumerable.Range(0, Max + 1).Select(n => n.ToString()).ToList() : Options;

    /// <summary>The answer in its proper form ("2", "Yes", the option as written), or null if it is not one of them.</summary>
    public string? Normalise(string? value) =>
        value is null ? null : Answers.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Numbers = ["none", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"];

    private static readonly string[] YesWords = ["yes", "yep", "yeah", "y", "sure", "definitely", "count me in", "absolutely"];
    private static readonly string[] NoWords = ["no", "nope", "n", "not this time", "can't", "cannot", "won't", "unable"];

    /// <summary>
    /// Reads an answer out of a reply written in words ("Two please", "None for us", "Yes!"). Only a clear answer
    /// counts: anything ambiguous returns null and stays an ordinary reply for staff to read.
    /// </summary>
    public string? ReadFrom(string reply)
    {
        var text = Regex.Replace(reply.ToLowerInvariant(), @"[^a-z0-9%.' ]+", " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (Kind)
        {
            case PromptKind.Count:
            {
                var found = words.Select(w => int.TryParse(w, out var n) ? n : Array.IndexOf(Numbers, w) is var i and >= 0 ? i : (int?)null)
                    .Where(n => n is not null).Distinct().ToList();
                if (words.Contains("just") && words.Contains("me") && found.Count == 0) found = [1]; // "Just me this time"
                if (found.Count == 0 && (text.Contains("none") || words.Contains("nil") || text.Contains("not this time"))) found = [0];
                return found is [var only] && only <= Max ? only.ToString() : null;
            }

            case PromptKind.YesNo:
            {
                var yes = YesWords.Any(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b"));
                var no = NoWords.Any(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b"));
                return yes == no ? null : yes ? "Yes" : "No";
            }

            default:
            {
                var matches = Options.Where(o => text.Contains(o.ToLowerInvariant())).ToList();
                // An option inside a longer one ("5%" inside "2.5%") gives way to the longer one.
                var outermost = matches.Where(m => !matches.Any(o => o != m && o.Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
                return outermost is [var one] ? one : null;
            }
        }
    }
}

/// <summary>How an answer was given: tapped in the portal, read from a reply, or recorded by staff.</summary>
public enum AnswerSource
{
    Portal,
    ReadFromReply,
    Staff,
}

/// <summary>
/// One owner's answer to a message's question: the latest one counts, so changing it replaces it. The words they
/// wrote (or the button they tapped) are a reply in the thread as well; this is the data the tally counts.
/// </summary>
public sealed class PromptAnswer
{
    /// <summary>For EF Core materialisation.</summary>
    private PromptAnswer() => (Value, RecordedBy) = (null!, null!);

    public PromptAnswer(StreamItem message, Guid partyId, string value, AnswerSource source, string recordedBy, DateTimeOffset at)
    {
        if (message.Prompt is null) throw new DomainException("That message doesn't ask a question.");
        TenantId = message.TenantId;
        StreamItemId = message.Id;
        PartyId = partyId;
        (Value, RecordedBy) = (null!, null!);
        Change(message, value, source, recordedBy, at);
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid StreamItemId { get; }

    public Guid PartyId { get; }

    public string Value { get; private set; }

    public AnswerSource Source { get; private set; }

    /// <summary>The owner's own name, or the staff member who recorded it for them.</summary>
    public string RecordedBy { get; private set; }

    public DateTimeOffset AnsweredAt { get; private set; }

    public void Change(StreamItem message, string value, AnswerSource source, string recordedBy, DateTimeOffset at)
    {
        if (message.Id != StreamItemId) throw new DomainException("An answer belongs to its own question.");
        Value = message.Prompt?.Normalise(value) ?? throw new DomainException($"\"{value}\" isn't one of the answers to \"{message.Prompt?.Question}\".");
        (Source, RecordedBy, AnsweredAt) = (source, recordedBy, at);
    }
}
