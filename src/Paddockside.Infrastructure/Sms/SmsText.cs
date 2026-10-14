using System.Text;

namespace Paddockside.Infrastructure.Sms;

/// <summary>
/// The words of an update text (messaging-channels.md §4.2): a notification first, never the whole message. Who it
/// is from, the horse, a short summary, then the link that opens the message and signs them in, and that replying
/// works. At most three segments; the summary is what gets cut, never the link.
/// </summary>
public static class SmsText
{
    public const int MaxSegments = 3;

    // GSM-7 basic set: texts in it fit 153 characters a segment when split; anything else (an emoji, a curly quote)
    // makes the whole text UCS-2 at 67 a segment.
    private const string Gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    public static string ForUpdate(string tenantName, string horseName, string? title, string body, string? link)
    {
        var head = $"{tenantName}: {horseName}{(string.IsNullOrWhiteSpace(title) ? "" : $" - {Plain(title)}")}.";
        var tail = link is null ? "Reply to this text to answer." : $"Open: {link} or reply to this text.";
        var summary = Plain(body);

        var full = $"{head} {summary} {tail}";
        var limit = Limit(full);
        if (full.Length <= limit) return full;

        var room = limit - head.Length - tail.Length - 2 - 3; // two spaces, "..."
        if (room < 20) return $"{head} {tail}"; // a very long title: drop the summary, keep the link
        var cut = summary[..room];
        var space = cut.LastIndexOf(' ');
        if (space > room / 2) cut = cut[..space];
        return $"{head} {cut.TrimEnd(' ', ',', '.', ';', ':', '-')}... {tail}";
    }

    /// <summary>How many segments a text takes, for the 3-segment rule and the cost count.</summary>
    public static int Segments(string text)
    {
        var gsm = IsGsm(text);
        var (single, multi) = gsm ? (160, 153) : (70, 67);
        var length = text.Length; // extended GSM characters (such as € or {) would count double; Plain() produces none of them
        return length <= single ? 1 : (length + multi - 1) / multi;
    }

    private static int Limit(string text) => IsGsm(text) ? 153 * MaxSegments : 67 * MaxSegments;

    private static bool IsGsm(string text) => text.All(Gsm.Contains);

    /// <summary>One line of plain text: paragraphs joined, curly quotes and dashes made plain so the text stays GSM.</summary>
    private static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            builder.Append(c switch
            {
                '\n' or '\t' => ' ',
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                '…' => '.',
                _ => c,
            });
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
