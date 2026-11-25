using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Media;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Account;

/// <summary>
/// How a business's emails look: its logo, colour and the details at the foot (David, 25 Nov 2026). Changed by its
/// tenant admins on the Account page, or by an operator in the console; the rules are the same.
/// </summary>
public static class Branding
{
    public sealed record BrandView(string Name, string? LogoUrl, int? LogoWidth, int? LogoHeight, string? Colour, string? Footer, string StandardColour,
        string? SquareLogoUrl = null);

    public sealed record LogoSaved(BrandView Brand, string? Warning);

    public sealed record LookRequest(string? Colour, string? Footer);

    /// <summary>A sample email as it would look with choices not saved yet.</summary>
    public sealed record SampleRequest(string? Key, string? Colour, string? Footer);

    public sealed record SampleView(string Subject, string Html);

    public static BrandView View(Tenant t) => new(t.Name, t.LogoUrl, t.LogoWidth, t.LogoHeight, t.BrandColour, t.FooterDetails, EmailPalette.Standard, t.SquareLogoUrl);

    public static async Task<IResult> SaveLookAsync(PaddocksideDbContext db, LookRequest request, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        try
        {
            tenant.SetEmailLook(request.Colour, request.Footer);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(View(tenant));
    }

    /// <summary>
    /// A new logo, wide or square, from a multipart form's <c>file</c>. Each upload has an address of its own, so emails already sent keep
    /// the logo they went out with.
    /// </summary>
    public static async Task<IResult> SaveLogoAsync(PaddocksideDbContext db, HttpRequest request, IMediaStore store, ILogger logger, CancellationToken cancellationToken, bool square = false)
    {
        if (!request.HasFormContentType) return Problem("Choose the logo file.");
        var form = await new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = Pictures.MaxLogoBytes + 64 * 1024 }).ReadFormAsync(cancellationToken);
        if (form.Files.FirstOrDefault(f => f.Length > 0) is not { } file) return Problem("Choose the logo file.");
        if (file.Length > Pictures.MaxLogoBytes) return Problem($"That file is too big: the most is {Pictures.MaxLogoBytes / 1024 / 1024} MB.");

        PreparedLogo? logo;
        using (var read = new MemoryStream())
        {
            await file.CopyToAsync(read, cancellationToken);
            try
            {
                logo = square ? Pictures.SquareLogo(read.ToArray()) : Pictures.Logo(read.ToArray());
            }
            catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException)
            {
                logger.LogError(ex, "The picture library could not be loaded.");
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Logos can't be processed on this server at the moment. Nothing was changed.");
            }
        }

        if (logo is null) return Problem("That isn't a PNG or JPG picture. Other kinds don't show everywhere (an SVG or PDF logo needs saving as a PNG).");

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var id = Guid.NewGuid();
        using (var png = new MemoryStream(logo.Png))
            await store.SaveAsync(LogoBlob(tenant.Id, id), png, "image/png", cancellationToken);
        if (square) tenant.SetSquareLogo($"/brand/{tenant.Id}/{id}.png");
        else tenant.SetLogo($"/brand/{tenant.Id}/{id}.png", logo.Width, logo.Height);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new LogoSaved(View(tenant), logo.Warning));
    }

    public static async Task<IResult> RemoveLogoAsync(PaddocksideDbContext db, CancellationToken cancellationToken, bool square = false)
    {
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        if (square) tenant.RemoveSquareLogo();
        else tenant.RemoveLogo();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(View(tenant));
    }

    public static async Task<IResult> SampleAsync(PaddocksideDbContext db, SampleRequest request, OwnerEmailRenderer renderer, string baseUrl, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        try
        {
            tenant.SetEmailLook(request.Colour, request.Footer); // shown, not saved
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        var samples = EmailSamples.For(tenant, renderer, baseUrl, clock.GetUtcNow());
        var sample = samples.FirstOrDefault(s => s.Key == request.Key) ?? samples[0];
        return Results.Ok(new SampleView(sample.Email.Subject, sample.Email.Html));
    }

    /// <summary>The address links in a sample use: the portal's, or the one this request came to when none is configured.</summary>
    public static string BaseUrl(EmailOptions options, HttpRequest request) =>
        options.PortalBaseUrl is { Length: > 0 } root ? root.TrimEnd('/') : $"{request.Scheme}://{request.Host}";

    private static string LogoBlob(Guid tenantId, Guid logoId) => $"{tenantId}/brand/{logoId}.png";

    /// <summary>
    /// The pictures in emails, for mail programs to fetch with nobody signed in. Only ever these two kinds, each stored
    /// under its own folder by us, so nothing else in storage can be reached; and no database, so opening an email never
    /// wakes it.
    /// </summary>
    public static void MapEmailPictures(this IEndpointRouteBuilder app)
    {
        app.MapGet("/brand/{tenantId:guid}/{logoId:guid}.png", (Guid tenantId, Guid logoId, HttpContext http, IMediaStore store, CancellationToken ct) =>
            PictureAsync(http, store, LogoBlob(tenantId, logoId), "image/png", TimeSpan.FromDays(365), ct));
        app.MapGet("/photo/{tenantId:guid}/{token}.jpg", (Guid tenantId, string token, HttpContext http, IMediaStore store, CancellationToken ct) =>
            MediaAsset.IsEmailToken(token)
                ? PictureAsync(http, store, MediaAsset.EmailBlobName(tenantId, token), "image/jpeg", TimeSpan.FromDays(30), ct)
                : Task.FromResult(Results.NotFound()));
        app.MapGet("/email-preview", () => Mailouts.MailoutEndpoints.Page("This is a preview", "<p>Buttons and links in preview emails don't do anything. In a real email this one would.</p>"));
    }

    private static async Task<IResult> PictureAsync(HttpContext http, IMediaStore store, string blob, string type, TimeSpan keep, CancellationToken cancellationToken)
    {
        if (await store.OpenReadAsync(blob, cancellationToken) is not { } stream) return Results.NotFound();
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        http.Response.Headers[HeaderNames.CacheControl] = $"public, max-age={(int)keep.TotalSeconds}, immutable";
        http.Response.Headers["X-Robots-Tag"] = "noindex";
        return Results.Stream(stream, type);
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
