using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Photos, video and voice notes staff add to an event (data-model.md MEDIA_ASSET): kept privately, shown to exactly
/// who may see the item they belong to, played from any point, and never served as anything but media. Shares the
/// sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record UploadedMedia(Guid Id, int Files, int Recipients);

    private sealed record MediaFile(Guid Id, string Kind, string ContentType, string FileName, long Length);

    private sealed record ItemWithMedia(Guid Id, string Kind, string? Title, string Body, List<MediaFile>? Media);

    private sealed record PageWithMedia(List<ItemWithMedia> Items);

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)), 0xFF, 0xD9];

    private static MultipartFormDataContent Upload(string scope, string caption, params (string Name, string Type, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(scope), "scope" }, { new StringContent(caption), "caption" } };
        foreach (var (name, type, bytes) in files)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(content, "files", name);
        }

        return form;
    }

    [Fact]
    public async Task Staff_share_photos_with_owners_who_see_them_and_nobody_else_can()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var posted = await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media",
            Upload("Owners", "Cantering this morning.", ("canter.jpg", "image/jpeg", Jpeg), ("walk.jpg", "image/jpeg", Jpeg)));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var uploaded = (await posted.Content.ReadFromJsonAsync<UploadedMedia>())!;
        Assert.Equal(2, uploaded.Files);

        // Ann sees the photos on the race, titled for her, and can open them.
        using var ann = await SignInAsync(seed.AnnEmail);
        var item = (await ann.GetFromJsonAsync<PageWithMedia>($"/api/my/events/{seed.EventId}"))!.Items.Single(i => i.Id == uploaded.Id);
        Assert.Equal(("New photos", "Cantering this morning."), (item.Title, item.Body));
        var photo = item.Media!.First();
        Assert.Equal(("Photo", "image/jpeg", "canter.jpg"), (photo.Kind, photo.ContentType, photo.FileName));

        var file = await ann.GetAsync($"/api/media/{photo.Id}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Contains("nosniff", file.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(Jpeg, await file.Content.ReadAsByteArrayAsync());

        // Video seeks: a part of the file on request.
        var part = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{photo.Id}");
        part.Headers.Range = new RangeHeaderValue(0, 9);
        var partial = await ann.SendAsync(part);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(10, (await partial.Content.ReadAsByteArrayAsync()).Length);

        // Kept for staff: Ann sees neither the item nor its file.
        var internalPost = (await (await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media",
            Upload("Internal", "Vet's photo of the knee.", ("knee.jpg", "image/jpeg", Jpeg)))).Content.ReadFromJsonAsync<UploadedMedia>())!;
        Assert.Equal(0, internalPost.Recipients);
        var staffPage = (await staff.GetFromJsonAsync<PageWithMedia>($"/api/events/{seed.EventId}"))!;
        var knee = staffPage.Items.Single(i => i.Id == internalPost.Id).Media!.Single();
        Assert.DoesNotContain((await ann.GetFromJsonAsync<PageWithMedia>($"/api/my/events/{seed.EventId}"))!.Items, i => i.Id == internalPost.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/media/{knee.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/media/{knee.Id}")).StatusCode);

        // Signed out, nothing; another tenant's staff, nothing.
        using var anonymous = Api.CreateClient();
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/media/{photo.Id}")).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"/api/media/{photo.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_photos_video_and_voice_notes_are_kept_and_not_from_a_viewer_or_onto_a_draft()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // A web page dressed as an upload is refused, whatever it's called.
        var page = await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media",
            Upload("Owners", "", ("photo.html", "text/html", "<script>alert(1)</script>"u8.ToArray())));
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Contains("isn't a photo, video or voice note", await page.Content.ReadAsStringAsync());

        // A phone's voice memo with no type is known by its extension.
        var memo = await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media",
            Upload("Owners", "", ("memo.m4a", "application/octet-stream", [1, 2, 3, 4])));
        Assert.Equal(HttpStatusCode.Created, memo.StatusCode);
        var memoId = (await memo.Content.ReadFromJsonAsync<UploadedMedia>())!.Id;
        var memoItem = (await staff.GetFromJsonAsync<PageWithMedia>($"/api/events/{seed.EventId}"))!.Items.Single(i => i.Id == memoId);
        Assert.Equal(("A voice note", "Audio"), (memoItem.Title, memoItem.Media!.Single().Kind));

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media", Upload("Owners", "Nothing attached"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostFormApiAsync($"/api/events/{seed.EventId}/media", Upload("Trainer", "", ("a.jpg", "image/jpeg", Jpeg)))).StatusCode);

        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostFormApiAsync($"/api/events/{seed.EventId}/media", Upload("Owners", "", ("a.jpg", "image/jpeg", Jpeg)))).StatusCode);

        var draft = (await (await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "Veterinary", title = "Scope", keyDate = (string?)null, open = false }))
            .Content.ReadFromJsonAsync<CreatedEvent>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostFormApiAsync($"/api/events/{draft}/media", Upload("Owners", "", ("a.jpg", "image/jpeg", Jpeg)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await staff.PostFormApiAsync($"/api/events/{draft}/media", Upload("Internal", "", ("a.jpg", "image/jpeg", Jpeg)))).StatusCode);
    }
}
