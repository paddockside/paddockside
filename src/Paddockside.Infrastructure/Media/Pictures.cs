using SkiaSharp;

namespace Paddockside.Infrastructure.Media;

/// <summary>A logo ready to store: a PNG, the size to show it at, and anything staff should know about it.</summary>
public sealed record PreparedLogo(byte[] Png, int Width, int Height, string? Warning);

/// <summary>
/// Pictures for emails. Each is decoded and drawn again, never passed through: a file that only claims to be a picture
/// is refused, and a photo loses its location and camera details before it goes anywhere public.
/// </summary>
public static class Pictures
{
    /// <summary>The box a logo is shown in at the top of an email.</summary>
    public const int LogoWidth = 220, LogoHeight = 72;

    /// <summary>The largest logo file accepted.</summary>
    public const long MaxLogoBytes = 5L * 1024 * 1024;

    /// <summary>A larger photo is left out of the email (its button still opens it).</summary>
    public const long MaxPhotoBytes = 40L * 1024 * 1024;

    /// <summary>Twice the 600 pixels an email shows it at.</summary>
    private const int PhotoWidth = 1200;

    /// <summary>A tall phone photo is trimmed to this share of its width, so the words aren't pushed off the screen.</summary>
    private const double TallestPhoto = 4.0 / 3;

    private const long MaxLogoPixels = 20_000_000, MaxPhotoPixels = 100_000_000;

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>A PNG or JPG as a logo: its empty edges trimmed, at most twice the size it's shown at. Null if it isn't one.</summary>
    public static PreparedLogo? Logo(byte[] file)
    {
        using var source = Decode(file, MaxLogoPixels, null, SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg);
        if (source is null) return null;

        var area = Trimmed(source);
        var shown = Math.Min(1.0, Math.Min((double)LogoWidth / area.Width, (double)LogoHeight / area.Height));
        var (width, height) = (Math.Max(1, (int)Math.Round(area.Width * shown)), Math.Max(1, (int)Math.Round(area.Height * shown)));
        var sharp = Math.Min(2.0, (double)area.Width / width);
        using var drawn = Draw(source, area, Math.Max(1, (int)Math.Round(width * sharp)), Math.Max(1, (int)Math.Round(height * sharp)), SKColors.Transparent);
        using var data = drawn.Encode(SKEncodedImageFormat.Png, 100);
        return new PreparedLogo(data.ToArray(), width, height,
            VeryLight(source, area) ? "This logo is very light, so it's hard to see on the white at the top of an email. Upload the version for a white background if there is one."
            : area.Width < LogoWidth && area.Height < LogoHeight ? "This logo is small, so it may look soft on a phone. A larger file would be sharper."
            : null);
    }

    /// <summary>A photo as a JPG to show in an email: the right way up, small, with nothing but the picture. Null if it can't be read.</summary>
    public static byte[]? ForEmail(byte[] file)
    {
        using var source = Decode(file, MaxPhotoPixels, PhotoWidth, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp, SKEncodedImageFormat.Gif);
        if (source is null) return null;

        var keep = Math.Min(source.Height, (int)(source.Width * TallestPhoto));
        var area = SKRectI.Create(0, (source.Height - keep) / 2, source.Width, keep);
        var width = Math.Min(PhotoWidth, area.Width);
        using var drawn = Draw(source, area, width, Math.Max(1, (int)Math.Round((double)area.Height * width / area.Width)), SKColors.White);
        using var data = drawn.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    /// <summary>Decodes one of the formats allowed, turned the way the camera was held.</summary>
    private static SKBitmap? Decode(byte[] file, long maxPixels, int? smallest, params SKEncodedImageFormat[] allowed)
    {
        using var data = SKData.CreateCopy(file);
        using var codec = SKCodec.Create(data);
        if (codec is null || !allowed.Contains(codec.EncodedFormat)) return null;
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > maxPixels) return null;
        // A phone photo is read at a smaller size where its format allows, so a big one doesn't use a lot of memory.
        var size = smallest is { } least && Math.Min(info.Width, info.Height) > least
            ? codec.GetScaledDimensions((float)least / Math.Min(info.Width, info.Height)) : new SKSizeI(info.Width, info.Height);

        var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        return Upright(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft) return bitmap;
        float w = bitmap.Width, h = bitmap.Height;
        var sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        // Each turns stored (x, y) into shown (x, y): across = a·x + b·y + c, down = d·x + e·y + f.
        var turn = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var turned = new SKBitmap(new SKImageInfo(sideways ? bitmap.Height : bitmap.Width, sideways ? bitmap.Width : bitmap.Height, bitmap.ColorType, bitmap.AlphaType));
        using (var canvas = new SKCanvas(turned))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(turn);
            canvas.DrawBitmap(bitmap, 0, 0, Smooth, null);
        }

        bitmap.Dispose();
        return turned;
    }

    private static SKImage Draw(SKBitmap source, SKRectI area, int width, int height, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(background);
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, SKRect.Create(area.Left, area.Top, area.Width, area.Height), SKRect.Create(0, 0, width, height), Smooth, null);
        return surface.Snapshot();
    }

    /// <summary>The part that isn't empty edge: what differs from the corner's colour (or from clear).</summary>
    private static SKRectI Trimmed(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var corner = pixels[0];
        bool Empty(SKColor p) => corner.Alpha < 8 ? p.Alpha < 8
            : Math.Abs(p.Red - corner.Red) + Math.Abs(p.Green - corner.Green) + Math.Abs(p.Blue - corner.Blue) + Math.Abs(p.Alpha - corner.Alpha) < 24;
        // A coloured block is the logo's own background, kept; only white or clear edges are trimmed.
        if (corner.Alpha >= 8 && (corner.Red < 245 || corner.Green < 245 || corner.Blue < 245)) return bitmap.Info.Rect;

        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (Empty(pixels[y * bitmap.Width + x])) continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }

        return right < 0 ? bitmap.Info.Rect : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>Almost all of what's drawn is near white, on a clear background.</summary>
    private static bool VeryLight(SKBitmap bitmap, SKRectI area)
    {
        long drawn = 0, light = 0;
        var clear = bitmap.GetPixel(0, 0).Alpha < 64;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Alpha < 64) { clear = true; continue; }
                drawn++;
                if (0.2126 * p.Red + 0.7152 * p.Green + 0.0722 * p.Blue > 215) light++;
            }

        return clear && drawn > 0 && light > drawn * 0.9;
    }
}
