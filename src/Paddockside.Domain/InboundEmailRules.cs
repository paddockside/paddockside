using System.Text.RegularExpressions;

namespace Paddockside.Domain;

/// <summary>
/// An address on the inbound subdomain, read back (messaging-channels.md §3.1):
/// <c>r-{token}@{tenant}.in.…</c> is a recipient's reply (tier 1), <c>{horse-slug}@{tenant}.in.…</c> a horse's inbox
/// (tier 2), and <c>{tenant}@in.…</c> the tenant's catch-all.
/// </summary>
public sealed record InboundAddress(string Address, string TenantSlug, string? LocalPart)
{
    public static InboundAddress? Parse(string? address, string inboundDomain)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var at = address.Trim().ToLowerInvariant().LastIndexOf('@');
        if (at <= 0) return null;
        var normalised = address.Trim().ToLowerInvariant();
        var (local, domain) = (normalised[..at], normalised[(at + 1)..]);
        var suffix = "." + inboundDomain.ToLowerInvariant();

        if (domain == inboundDomain.ToLowerInvariant()) return new InboundAddress(normalised, local, null);
        if (!domain.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var tenant = domain[..^suffix.Length];
        return tenant.Length == 0 || tenant.Contains('.') ? null : new InboundAddress(normalised, tenant, local);
    }

    /// <summary>The reply token, when the local part is a well-formed <c>r-{token}</c>; a malformed one is no token.</summary>
    public string? RecipientToken =>
        LocalPart is { } local && local.StartsWith("r-", StringComparison.Ordinal) && RoutingToken.IsWellFormed(local[2..]) ? local[2..] : null;

    /// <summary>The horse slug, when the local part could be one and is not a reply token.</summary>
    public string? HorseSlugPart => RecipientToken is null && HorseSlug.IsWellFormed(LocalPart) ? LocalPart : null;
}

/// <summary>
/// The display body: what the person actually wrote this time (messaging-channels.md §3.3). Quoted history,
/// our own email's text and signatures are cut; the full body is kept on the raw record.
/// </summary>
public static partial class QuotedText
{
    /// <summary>Lines of our own outbound emails: a quoted copy of one is always removed.</summary>
    public static readonly IReadOnlyList<string> OwnTemplateLines =
    [
        "Reply to this email to answer.",
        "You are receiving this because you are an owner of",
    ];

    public static string Strip(string? text, IEnumerable<string>? ownLines = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var own = (ownLines ?? OwnTemplateLines).ToList();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var kept = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (StartsQuotedHistory(lines, i) || IsSignatureStart(trimmed) || own.Any(o => trimmed.Contains(o, StringComparison.OrdinalIgnoreCase)))
                break;
            if (trimmed.StartsWith('>')) continue; // an interleaved quote; the reply text around it stays
            kept.Add(line.TrimEnd());
        }

        var result = string.Join('\n', kept).Trim();
        // Everything was quoted (or the reply is above a marker we misread): show the whole body rather than nothing.
        return result.Length > 0 ? result : text.Trim();
    }

    private static bool StartsQuotedHistory(string[] lines, int i)
    {
        var line = lines[i].Trim();
        if (OriginalMessage().IsMatch(line) || ForwardedMessage().IsMatch(line)) return true;

        // "On Mon, 5 Oct 2026 at 10:02, Kate <kate@…> wrote:", which mail apps often wrap over two or three lines.
        if (line.StartsWith("On ", StringComparison.Ordinal))
        {
            for (var j = i; j < Math.Min(lines.Length, i + 3); j++)
                if (lines[j].TrimEnd().EndsWith("wrote:", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Outlook: a "From:" block followed by Sent/Date/To within a few lines, often after a rule of underscores.
        if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase) || Underscores().IsMatch(line))
        {
            for (var j = i + 1; j < Math.Min(lines.Length, i + 5); j++)
            {
                var next = lines[j].TrimStart();
                if (next.StartsWith("Sent:", StringComparison.OrdinalIgnoreCase) || next.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool IsSignatureStart(string line) =>
        line is "--" or "-- " || line.StartsWith("Sent from my ", StringComparison.OrdinalIgnoreCase)
                                || line.StartsWith("Get Outlook for ", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^-{2,}\s*Original Message\s*-{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^-{2,}\s*Forwarded message\s*-{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardedMessage();

    [GeneratedRegex(@"^_{10,}$")]
    private static partial Regex Underscores();
}

/// <summary>
/// Mail no person wrote (messaging-channels.md §3.4 Loops): auto-replies, out-of-office and bounces. Filed as
/// Ignored — never placed, never answered — so two robots can never email each other forever.
/// </summary>
public static class AutomaticMail
{
    private static readonly string[] AutoReplySubjects =
        ["out of office", "automatic reply", "auto:", "autoreply", "auto-reply", "auto reply", "away from the office", "on leave"];

    private static readonly string[] BounceSubjects =
        ["undeliverable", "undelivered mail", "delivery status notification", "mail delivery failed", "returned mail", "delivery has failed", "failure notice"];

    /// <summary>Why this is automatic mail, or null when a person (probably) wrote it.</summary>
    public static string? Reason(IReadOnlyList<(string Name, string Value)> headers, string? subject, string fromAddress)
    {
        string? Header(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.Trim();

        if (Header("Auto-Submitted") is { } autoSubmitted && !autoSubmitted.Equals("no", StringComparison.OrdinalIgnoreCase))
            return $"Automatic email (Auto-Submitted: {autoSubmitted}).";
        if (Header("X-Auto-Response-Suppress") is { } suppress
            && (suppress.Contains("All", StringComparison.OrdinalIgnoreCase) || suppress.Contains("OOF", StringComparison.OrdinalIgnoreCase)))
            return $"Automatic email (X-Auto-Response-Suppress: {suppress}).";
        if (Header("Precedence") is { } precedence && precedence.ToLowerInvariant() is "bulk" or "auto_reply" or "junk")
            return $"Automatic email (Precedence: {precedence}).";
        if (Header("X-Autoreply") is not null || Header("X-Autorespond") is not null)
            return "Automatic reply.";

        var local = fromAddress.Split('@')[0].ToLowerInvariant();
        if (local is "mailer-daemon" or "postmaster" || Header("Content-Type") is { } type && type.Contains("multipart/report", StringComparison.OrdinalIgnoreCase))
            return "A bounce: the mail system could not deliver something.";

        var s = subject?.Trim().ToLowerInvariant() ?? string.Empty;
        if (BounceSubjects.Any(s.StartsWith)) return "A bounce: the mail system could not deliver something.";
        if (AutoReplySubjects.Any(s.StartsWith)) return "An automatic reply (out of office).";
        return null;
    }

    /// <summary>The provider's spam verdict (messaging-channels.md §3.4): respected, never second-guessed.</summary>
    public static string? SpamVerdict(IReadOnlyList<(string Name, string Value)> headers)
    {
        var status = headers.FirstOrDefault(h => h.Name.Equals("X-Spam-Status", StringComparison.OrdinalIgnoreCase)).Value;
        if (status is null || !status.TrimStart().StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) return null;
        var score = headers.FirstOrDefault(h => h.Name.Equals("X-Spam-Score", StringComparison.OrdinalIgnoreCase)).Value;
        return score is null ? "Marked as spam by the mail provider." : $"Marked as spam by the mail provider (score {score.Trim()}).";
    }
}

/// <summary>Which attachments are not kept (messaging-channels.md §3.3–§3.4).</summary>
public static class InboundAttachmentRules
{
    public const int SignatureImageLimit = 20 * 1024;

    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".ps1", ".psm1", ".jar", ".cpl", ".hta", ".lnk", ".reg", ".dll", ".app", ".sh",
    };

    /// <summary>Why this attachment is dropped, or null to keep it.</summary>
    public static string? DropReason(string fileName, string contentType, long length, string? contentId)
    {
        if (Executable.Contains(Path.GetExtension(fileName)) || contentType.Contains("x-msdownload", StringComparison.OrdinalIgnoreCase))
            return "Executable attachment dropped for safety.";
        if (!string.IsNullOrEmpty(contentId) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && length < SignatureImageLimit)
            return "Small inline image (a signature logo) dropped.";
        return null;
    }
}
