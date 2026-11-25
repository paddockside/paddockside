using System.Globalization;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using T = Paddockside.Infrastructure.Email.EmailTokens;

namespace Paddockside.Infrastructure.Email;

/// <summary>Whose email it is and how it looks: the business's logo and colour, or Paddockside's own.</summary>
public sealed record EmailBrand(string Name, string? LogoUrl, int? LogoWidth, int? LogoHeight, string? Colour, string? FooterDetails)
{
    public static readonly EmailBrand Product = new(Senders.Product, null, null, null, null, null);

    public static EmailBrand Of(Tenant tenant) =>
        new(tenant.Name, tenant.LogoUrl, tenant.LogoWidth, tenant.LogoHeight, tenant.BrandColour, tenant.FooterDetails);
}

/// <summary>
/// The colours one email uses, from the business's brand colour (David, 25 Nov 2026). Whatever is chosen, the words on it
/// and in it stay readable: at least 4.5 to 1 (WCAG AA).
/// </summary>
/// <param name="Band">Behind the horse's name and on solid buttons.</param>
/// <param name="OnBand">The words on it.</param>
/// <param name="Line">The words and edge of an outlined button, on white.</param>
public sealed record EmailPalette(string Band, string OnBand, string Line)
{
    private const double Readable = 4.5;

    private const string Black = "black";

    /// <summary>Paddockside's own, for a business that hasn't chosen a colour.</summary>
    public static string Standard => T.ColorActionPrimaryBg;

    public static EmailPalette For(string? brandColour)
    {
        var band = brandColour ?? T.ColorActionPrimaryBg;
        // Black or white always reaches 4.5 on any colour; the softer near-black is used where it does too.
        var onBand = new[] { T.ColorTextInverse, T.ColorTextPrimary, Black }.First(c => c == Black || Contrast(band, c) >= Readable);
        return new EmailPalette(band, onBand, Contrast(band, T.ColorSurfaceRaised) >= Readable ? band : T.ColorTextPrimary);
    }

    public static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        if (hex == Black) return 0;
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}

/// <summary>The same token colours, for the plain pages links in emails open (reply to an invitation, answer, unsubscribe).</summary>
public static class PageColours
{
    public const string Background = T.ColorSurfaceDefault, Card = T.ColorSurfaceRaised, Text = T.ColorTextPrimary, Note = T.ColorTextMuted, Line = T.ColorBorderSubtle,
        Field = T.ColorBorderStrong, Button = T.ColorActionPrimaryBg, OnButton = T.ColorActionPrimaryFg;
}

/// <summary>What a media email shows: the first photo's address, where there's one a mail program can show, and what there is.</summary>
public sealed record EmailMedia(string? PhotoUrl, IReadOnlyList<MediaKind> Kinds);
