using System.Globalization;
using System.Text;

namespace Paddockside.Domain;

/// <summary>
/// The local part of a horse's own inbox, <c>{horse-slug}@{tenant}.in.{product}</c> (messaging-channels.md §3.1):
/// the name lower-cased, accents removed, anything else between letters and digits becoming one hyphen.
/// "Bel Esprit" → <c>bel-esprit</c>, "O'Reilly's Pride" → <c>oreillys-pride</c>.
/// </summary>
public static class HorseSlug
{
    public const int MaxLength = 64;

    public static string? From(string name)
    {
        var slug = new StringBuilder();
        var pendingHyphen = false;
        foreach (var c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’') continue; // O'Reilly → oreilly, not o-reilly
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && slug.Length > 0) slug.Append('-');
                pendingHyphen = false;
                slug.Append(c);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var result = slug.Length > MaxLength ? slug.ToString(0, MaxLength).TrimEnd('-') : slug.ToString();
        return result.Length == 0 ? null : result;
    }

    public static bool IsWellFormed(string? slug) =>
        slug is { Length: > 0 and <= MaxLength } && From(slug) == slug;
}
