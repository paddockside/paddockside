using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Media;
using SkiaSharp;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// How emails look (David, 25 Nov 2026): the business's logo and colour, the photo in a photo email, answer buttons that
/// need no sign-in, and messages signed with a person's name. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record Brand(string Name, string? LogoUrl, int? LogoWidth, int? LogoHeight, string? Colour, string? Footer);

    private sealed record SavedLogo(Brand Brand, string? Warning);

    private sealed record Account(string? YourName, bool CanChangeBrand, Brand Brand);

    private sealed record SampleEmail(string Subject, string Html);

    /// <summary>A picture: the left half one colour and the right another, with a clear or white margin if asked.</summary>
    private static byte[] Picture(int width, int height, SKEncodedImageFormat format, SKColor left, SKColor right, int margin = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Transparent);
        using var a = new SKPaint { Color = left };
        using var b = new SKPaint { Color = right };
        surface.Canvas.DrawRect(margin, margin, width / 2 - margin, height - 2 * margin, a);
        surface.Canvas.DrawRect(width / 2, margin, width / 2 - margin, height - 2 * margin, b);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A phone photo held sideways: the camera's details (its "turn me" note and a place) ahead of the picture.</summary>
    private static byte[] PhonePhoto()
    {
        byte[] exif = [.. "Exif\0\0"u8, .. "MM\0*"u8, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0, .. "GPS-34.1234,151.5678"u8];
        var jpeg = Picture(400, 200, SKEncodedImageFormat.Jpeg, SKColors.Red, SKColors.Blue);
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2), .. exif, .. jpeg[2..]];
    }

    private static MultipartFormDataContent LogoForm(byte[] bytes, string name = "logo.png", string type = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { content, "file", name } };
    }

    [Fact]
    public async Task A_tenant_admin_sets_the_logo_and_colour_and_owners_emails_carry_them()
    {
        var seed = await SeedAsync();
        using var admin = await Api.SignedInAsync(seed.TenantId, MemberRole.TenantAdmin);

        // A wide logo with empty edges: trimmed, stored sharp, shown to fit.
        var saved = (await (await admin.PostFormApiAsync("/api/account/logo", LogoForm(Picture(1200, 600, SKEncodedImageFormat.Png, SKColors.Navy, SKColors.DarkGreen, margin: 100))))
            .Content.ReadFromJsonAsync<SavedLogo>())!;
        Assert.Null(saved.Warning);
        Assert.Equal((180, 72), (saved.Brand.LogoWidth, saved.Brand.LogoHeight)); // 1000 × 400, shown 72 high
        Assert.Equal(HttpStatusCode.OK, (await admin.PutApiAsync("/api/account/look", new { colour = "#7A1F2B", footer = "Sample Bloodstock Pty Ltd" })).StatusCode);

        // Anyone can fetch it: a mail program has no session. Nothing else in storage is reachable that way.
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var picture = await anyone.GetAsync(saved.Brand.LogoUrl);
        Assert.Equal(("image/png", HttpStatusCode.OK), (picture.Content.Headers.ContentType!.MediaType, picture.StatusCode));
        using (var stored = SKBitmap.Decode(await picture.Content.ReadAsByteArrayAsync())) Assert.Equal((360, 144), (stored.Width, stored.Height));
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/brand/{seed.TenantId}/{Guid.NewGuid()}.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/photo/{seed.TenantId}/{new string('2', 31)}.jpg")).StatusCode);

        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = Array.Empty<Guid>(), body = "Branded update." });
        await DispatchAsync();
        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains("Branded update.")).Email;
        Assert.Contains($"src=\"{ApiFactory.PortalBaseUrl}{saved.Brand.LogoUrl}\"", email.HtmlBody);
        Assert.Contains("background:#7a1f2b", email.HtmlBody);
        Assert.Contains("Sample Bloodstock Pty Ltd", email.HtmlBody);

        // A sample shows choices that aren't saved, and saves nothing.
        var sample = (await (await admin.PostApiAsync("/api/account/sample", new { key = "barrier", colour = "#123456", footer = "Not saved" })).Content.ReadFromJsonAsync<SampleEmail>())!;
        Assert.Contains("background:#123456", sample.Html);
        Assert.Contains("Not saved", sample.Html);
        var account = (await admin.GetFromJsonAsync<Account>("/api/account"))!;
        Assert.Equal(("#7a1f2b", "Sample Bloodstock Pty Ltd"), (account.Brand.Colour, account.Brand.Footer));

        // Not a picture email programs show; not a colour; not staff's to change; another business's is its own.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostFormApiAsync("/api/account/logo", LogoForm("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "logo.svg", "image/svg+xml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostFormApiAsync("/api/account/logo", LogoForm("<html>not a picture</html>"u8.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutApiAsync("/api/account/look", new { colour = "red;}body{", footer = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutApiAsync("/api/account/look", new { colour = "#000000", footer = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostFormApiAsync("/api/account/logo", LogoForm(Picture(300, 100, SKEncodedImageFormat.Png, SKColors.Navy, SKColors.Navy)))).StatusCode);
        using var other = await Api.SignedInAsync(db.A.TenantId, MemberRole.TenantAdmin);
        Assert.Null((await other.GetFromJsonAsync<Account>("/api/account"))!.Brand.LogoUrl);

        // A white logo would vanish on white: it's kept, and they're told.
        var pale = (await (await admin.PostFormApiAsync("/api/account/logo", LogoForm(Picture(800, 300, SKEncodedImageFormat.Png, SKColors.White, SKColors.White, margin: 40)))).Content.ReadFromJsonAsync<SavedLogo>())!;
        Assert.Contains("very light", pale.Warning);
        Assert.NotEqual(saved.Brand.LogoUrl, pale.Brand.LogoUrl); // emails already sent keep the logo they had
        Assert.Equal(HttpStatusCode.OK, (await anyone.GetAsync(saved.Brand.LogoUrl)).StatusCode);

        var removed = (await (await admin.PostApiAsync("/api/account/logo/remove", new { })).Content.ReadFromJsonAsync<Brand>())!;
        Assert.Null(removed.LogoUrl);
    }

    [Fact]
    public async Task Updates_are_signed_with_the_name_staff_set_and_never_their_address()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        async Task<string> Signed(string body)
        {
            await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
                new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = Array.Empty<Guid>(), body });
            await DispatchAsync();
            return Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains(body)).Email.TextBody;
        }

        Assert.Contains($"- The team at {seed.TenantName}", await Signed("Before a name."));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutApiAsync("/api/account/name", new { name = "someone@example.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PutApiAsync("/api/account/name", new { name = " Robin Sample " })).StatusCode);
        Assert.Equal("Robin Sample", (await staff.GetFromJsonAsync<Account>("/api/account"))!.YourName);
        Assert.Contains($"- Robin Sample, {seed.TenantName}", await Signed("After a name."));

        // Their mailout signature is its own thing, and is kept.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PutApiAsync("/api/mailouts/signature", new { text = "Regards, R" })).StatusCode);
        Assert.Equal("Robin Sample", (await staff.GetFromJsonAsync<Account>("/api/account"))!.YourName);
    }

    [Fact]
    public async Task A_photo_email_shows_the_first_photo_without_a_sign_in_and_nothing_but_the_picture()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media",
            Upload("Owners", "Trackwork today.", ("IMG_1.jpg", "image/jpeg", PhonePhoto()), ("IMG_2.jpg", "image/jpeg", PhonePhoto())));
        await DispatchAsync();

        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains("Trackwork today.")).Email;
        Assert.Contains("See all 2 photos", email.HtmlBody);
        var path = Regex.Match(email.HtmlBody, $"src=\"{ApiFactory.PortalBaseUrl}(/photo/[^\"]+)\"").Groups[1].Value;
        Assert.Matches($"^/photo/{seed.TenantId}/[a-z2-9]{{32}}\\.jpg$", path);
        var bobs = Api.Emails.Sent.Last(e => e.Email.ToName == "Bob Owner" && e.Email.TextBody.Contains("Trackwork today.")).Email;
        Assert.Contains(path, bobs.HtmlBody); // one copy, made once

        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var shown = await anyone.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        var bytes = await shown.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(bytes)); // where it was taken is not in the public copy
        using (var upright = SKBitmap.Decode(bytes))
        {
            Assert.Equal(200, upright.Width); // the right way up: 200 across, trimmed to 4:3 tall
            Assert.Equal(266, upright.Height);
            Assert.True(upright.GetPixel(100, 10).Red > 200 && upright.GetPixel(100, 255).Blue > 200);
        }

        // The original still needs a sign-in.
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var asset = await context.MediaAssets.SingleAsync(a => a.FileName == "IMG_1.jpg" && a.EmailToken != null);
            Assert.NotEqual(HttpStatusCode.OK, (await anyone.GetAsync($"/api/media/{asset.Id}")).StatusCode);
            Assert.Null((await context.MediaAssets.SingleAsync(a => a.FileName == "IMG_2.jpg")).EmailToken); // only the one shown
        }

        // A picture that can't be read goes without it, and still goes.
        await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media", Upload("Owners", "Unreadable today.", ("bad.jpg", "image/jpeg", Jpeg)));
        await DispatchAsync();
        var plain = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains("Unreadable today.")).Email;
        Assert.DoesNotContain("/photo/", plain.HtmlBody);
        Assert.Contains("See the photo", plain.HtmlBody);
    }

    [Fact]
    public async Task An_owner_answers_from_the_email_and_only_sending_the_form_counts()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var posted = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items", new
        {
            audience = "Owners", scope = "Owners", step = "Owners tickets", channel = "Preferred", namedParties = (Guid[]?)null,
            body = "Tickets are in.", prompt = new { kind = "Count", question = "How many would you like?", max = 6 },
        })).Content.ReadFromJsonAsync<Sent>())!;
        await DispatchAsync();
        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains("Tickets are in.")).Email;
        var link = new Uri(Regex.Match(email.TextBody, @"Answer here: (\S+)").Groups[1].Value);
        foreach (var answer in new[] { "0", "1", "2", "3", "more" }) Assert.Contains($"{link}?answer={answer}\"", email.HtmlBody);

        async Task<string?> AnswerAsync(Guid party)
        {
            await using var context = db.ContextFor(seed.TenantId);
            return await context.PromptAnswers.Where(a => a.StreamItemId == posted.Id && a.PartyId == party).Select(a => a.Value).SingleOrDefaultAsync();
        }

        // Opening it (as a mail scanner would) asks, and answers nothing.
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var page = await anyone.GetStringAsync($"{link.AbsolutePath}?answer=2");
        Assert.Contains("<option value=\"2\" selected>", page);
        Assert.Contains("Ann Owner", page);
        Assert.Contains("<option value=\"4\" selected>", await anyone.GetStringAsync($"{link.AbsolutePath}?answer=more"));
        Assert.Null(await AnswerAsync(seed.AnnId));

        async Task<string> Send(string path, string answer) =>
            await (await anyone.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["answer"] = answer }))).Content.ReadAsStringAsync();
        Assert.Contains("Thanks, Ann", await Send(link.AbsolutePath, "2"));
        Assert.Contains("Choose one of the answers", await Send(link.AbsolutePath, "99"));
        Assert.Equal("2", await AnswerAsync(seed.AnnId));
        Assert.Null(await AnswerAsync(seed.BobId)); // her link answers for her alone

        // It's her reply in the thread, as from anywhere else; changing it is another.
        await Send(link.AbsolutePath, "4");
        await using (var context = db.ContextFor(seed.TenantId))
        {
            Assert.Equal(["My answer: 2", "Changed my answer to 4"], await context.StreamItems.Where(i => i.InReplyToId == posted.Id).OrderBy(i => i.OccurredAt).Select(i => i.Body).ToListAsync());
            Assert.Equal(AnswerSource.EmailButton, (await context.PromptAnswers.SingleAsync(a => a.StreamItemId == posted.Id && a.PartyId == seed.AnnId)).Source);
        }

        // Made up, another business's name on it, or a message that asks nothing: not recognised.
        Assert.Contains("Link not recognised", await anyone.GetStringAsync(link.AbsolutePath[..^1] + (link.AbsolutePath[^1] == '2' ? "3" : "2")));
        await using (var context = db.ContextFor(db.A.TenantId))
        {
            var otherSlug = await context.Tenants.Select(t => t.Slug).SingleAsync();
            Assert.Contains("Link not recognised", await anyone.GetStringAsync($"/a/{otherSlug}/{link.Segments[^1]}"));
        }

        await using (var context = db.ContextFor(seed.TenantId))
        {
            var plain = await context.RoutingAddresses.Where(r => r.StreamItemId != posted.Id && r.Kind == RoutingAddressKind.Recipient && r.StreamItemId != null).Select(r => r.Token).FirstOrDefaultAsync();
            if (plain is not null) Assert.Contains("Link not recognised", await anyone.GetStringAsync($"{link.Segments[0]}{link.Segments[1]}{link.Segments[2]}{plain}"));

            // Someone who has left the horse can no longer answer.
            var horse = await context.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).SingleAsync(h => h.Id == seed.HorseId);
            context.Remove(horse.Interests.Single(i => i.PartyId == seed.AnnId && i.IsActive));
            await context.SaveChangesAsync();
        }

        Assert.Contains("Link not recognised", await Send(link.AbsolutePath, "1"));
        Assert.Equal("4", await AnswerAsync(seed.AnnId));
    }

    [Theory]
    [InlineData("#1e5a3c")]
    [InlineData("#c9a227")]
    [InlineData("#ffffff")]
    [InlineData("#000000")]
    [InlineData("#808080")]
    [InlineData("#e4002b")]
    [InlineData("#7f7fff")]
    public void Whatever_colour_is_chosen_the_words_can_be_read(string colour)
    {
        var palette = EmailPalette.For(colour);
        Assert.True(EmailPalette.Contrast(palette.Band, palette.OnBand) >= 4.5, $"{palette.OnBand} on {palette.Band}");
        Assert.True(EmailPalette.Contrast(palette.Line, "#ffffff") >= 4.5, $"{palette.Line} on white");
    }

    [Fact]
    public void A_huge_or_broken_picture_is_refused_not_read()
    {
        Assert.Null(Pictures.Logo([]));
        Assert.Null(Pictures.Logo(Picture(5000, 4100, SKEncodedImageFormat.Png, SKColors.Navy, SKColors.Navy))); // over 20 million pixels
        Assert.Null(Pictures.ForEmail("GIF89a"u8.ToArray()));
    }
}
