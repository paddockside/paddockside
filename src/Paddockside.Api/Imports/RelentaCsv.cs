using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Paddockside.Domain;

namespace Paddockside.Api.Imports;

/// <summary>One contact as Relenta exports it, read into what Paddockside keeps.</summary>
/// <param name="Mobile">The first Australian mobile among their numbers; the rest are <paramref name="OtherPhones"/>.</param>
/// <param name="Unsubscribed">"Unsubscribed" or "Do not mass-email" was set in Relenta.</param>
/// <param name="Groups">Their Relenta groups, trimmed, once each, without "All contacts".</param>
public sealed record RelentaContact(int Row, string Name, string? Email, string? Mobile, string? OtherPhones, string? Title, string? Company, string? Address,
    string? Notes, string? Website, string? Social, DateTimeOffset? Since, bool Unsubscribed, IReadOnlyList<string> Groups)
{
    /// <summary>Two people under one contact ("Nicole &amp; Craig"): imported as one client, as Relenta has them.</summary>
    public bool LooksLikeACouple => Name.Contains(" & ", StringComparison.Ordinal) || Name.Contains(" and ", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Relenta's contact export (CSV, with addresses running over several lines inside quotes). Nothing in a row is
/// dropped: every field Paddockside has a place for is kept, and the groups are kept on the client as they were.
/// </summary>
public static partial class RelentaCsv
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static readonly string[] Required = ["First", "Last", "Email"];

    /// <summary>The contacts in the file, or why it isn't a Relenta export.</summary>
    public static (IReadOnlyList<RelentaContact> Contacts, string? Problem) Read(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0) return ([], "The file is empty.");
        var header = rows[0].Select(h => h.Trim()).ToList();
        if (Required.Any(r => !header.Contains(r, StringComparer.OrdinalIgnoreCase)))
            return ([], "That doesn't look like a Relenta contacts export: it needs First, Last and Email columns.");
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? Get(List<string> row, string name) => Col(name) is var i && i >= 0 && i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;

        var contacts = new List<RelentaContact>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var email = Get(row, "Email")?.ToLowerInvariant();
            var name = string.Join(' ', new[] { Get(row, "First"), Get(row, "Last") }.OfType<string>());
            if (name.Length == 0) name = Get(row, "Company") ?? email ?? $"Relenta contact (row {r + 1})";

            var (mobile, others) = Phones(Get(row, "Phone"), Get(row, "Text"), Get(row, "Fax"));
            var social = string.Join('\n', new[] { "LinkedIn", "Facebook", "Twitter", "Messenger" }
                .Select(s => Get(row, s) is { } v ? $"{s}: {v}" : null).OfType<string>());
            var notes = string.Join("\n\n", new[] { Get(row, "Comment"), Get(row, "Tags") is { } tags ? $"Relenta tags: {tags}" : null }.OfType<string>());

            contacts.Add(new RelentaContact(r + 1, name, email, mobile, others, Get(row, "Title"), Get(row, "Company"), Address(Get(row, "Address")),
                notes.Length == 0 ? null : notes, Get(row, "Website"), social.Length == 0 ? null : social, Date(Get(row, "Creation date")),
                IsSet(Get(row, "Unsubscribed")) || IsSet(Get(row, "Do not mass-email")), Groups(Get(row, "Groups"))));
        }

        return (contacts, null);
    }

    /// <summary>What a group looks like, so the obvious ones are sorted for you: a horse already here, a list, or perhaps a horse.</summary>
    public enum GroupKind
    {
        /// <summary>The name of a horse already in Paddockside: its people are linked to it, share to confirm.</summary>
        Horse,

        /// <summary>Has words or a year that horses don't ("Owners", "Social", "Cup Week", "2023 Royal Ascot").</summary>
        List,

        /// <summary>Anything else: most likely a horse not set up in Paddockside (yet).</summary>
        HorseName,
    }

    private static readonly string[] ListWords =
    [
        "owners", "owner", "social", "cup", "golf", "tour", "tourists", "trip", "parade", "parades", "lunch", "box", "trainers", "test", "testing",
        "fund", "funds", "list", "purchasers", "group", "combined", "competition", "reunion", "betting", "view", "lovers", "shareholders",
        "breeding right", "attendees", "weekend", "brothers", "ascot", "slipper", "plate", "pedigree", "pedigrees", "client", "rehoming",
        "managers", "yearlings", "broodmares", "inbreeding", "contacts", "mated", "fillies", "horses", "runners", "lopa", "superfunds",
        "supefunds", "confirmed", "preliminary", "eve", "stallion share", "non-", "commercial", "leasing", "inc lessees",
    ];

    public static GroupKind Classify(string group, IReadOnlyDictionary<string, Guid> horses)
    {
        if (HorseFor(group, horses) is not null) return GroupKind.Horse;
        var lower = group.ToLowerInvariant();
        if (Year().IsMatch(group)) return GroupKind.List;
        return ListWords.Any(w => Regex.IsMatch(lower, $@"(^|[^a-z]){Regex.Escape(w)}([^a-z]|$)")) ? GroupKind.List : GroupKind.HorseName;
    }

    /// <summary>The horse a group names: its name exactly (any case, extra spaces), or with a note like "(Deceased)" left off.</summary>
    public static Guid? HorseFor(string group, IReadOnlyDictionary<string, Guid> horses)
    {
        var name = Normalise(group);
        if (horses.TryGetValue(name, out var id)) return id;
        var bare = Normalise(Note().Replace(group, ""));
        return bare.Length > 0 && horses.TryGetValue(bare, out var bareId) ? bareId : null;
    }

    public static string Normalise(string name) => Spaces().Replace(name.Trim(), " ").ToLowerInvariant();

    private static List<string> Groups(string? text) =>
        (text ?? "").Split(';').Select(g => Spaces().Replace(g.Trim(), " ")).Where(g => g.Length > 0 && !g.Equals("All contacts", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(g => g.ToLowerInvariant()).ToList();

    /// <summary>Relenta keeps every number in one field ("8883 1066 (Work);0410 340 321"): the first mobile is texted, the rest kept.</summary>
    private static (string? Mobile, string? Others) Phones(params string?[] fields)
    {
        string? mobile = null;
        var others = new List<string>();
        foreach (var (entry, isFax) in fields.SelectMany((f, i) => (f ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => (e, i == 2))))
        {
            var number = Note().Replace(entry, "").Trim();
            if (!isFax && mobile is null && PhoneNumbers.Normalise(number) is { } normalised && normalised.StartsWith("+614", StringComparison.Ordinal)) mobile = normalised;
            else if (!others.Contains(entry)) others.Add(isFax ? $"{entry} (Fax)" : entry);
        }

        return (mobile, others.Count == 0 ? null : string.Join("; ", others));
    }

    /// <summary>"(Address)\n46 Somersby Cct, Acacia Gardens 2763" as just the address.</summary>
    private static string? Address(string? text)
    {
        if (text is null) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !Regex.IsMatch(l, @"^\([A-Za-z ]+\)$")).ToList();
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>Relenta's creation date, month first ("05-22-12"), as midnight in Sydney.</summary>
    private static DateTimeOffset? Date(string? text)
    {
        if (text is null || !DateTime.TryParseExact(text, ["MM-dd-yy", "MM-dd-yyyy", "M-d-yy", "M/d/yy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return null;
        return new DateTimeOffset(day.Date, Sydney.GetUtcOffset(day.Date));
    }

    private static bool IsSet(string? text) => text is not null && text.Trim().ToLowerInvariant() is not ("0" or "no" or "false" or "n");

    /// <summary>RFC 4180: quoted fields may hold commas, doubled quotes and line breaks.</summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    [GeneratedRegex(@"(19|20)\d{2}")]
    private static partial Regex Year();

    [GeneratedRegex(@"\s*\([^)]*\)\s*|\s+-\s+.*$")]
    private static partial Regex Note();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
