using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Sending an update from a phone (David, 25 Nov 2026): files up in pieces, any mix on one update, the horse's standing
/// General updates when it isn't about one event, and the same update sent twice arriving once. Shares the sign-in tests'
/// seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record UpdateEventOption(Guid? Id, string Title, List<string> Steps);

    private sealed record UpdateChoices(string Horse, bool Managed, List<UpdateEventOption> Events, List<NewHorse> Owners);

    private sealed record UploadStarted(Guid Id, int PartBytes, int Parts);

    private sealed record UpdateSent(Guid ItemId, Guid EventId, int Recipients);

    private static async Task<HttpResponseMessage> PutPartAsync(HttpClient staff, Guid upload, int part, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{upload}/parts/{part}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Paddockside-Request", "1");
        return await staff.SendAsync(request);
    }

    private static async Task<Guid> UploadedAsync(HttpClient staff, Guid horseId, string name, string type, byte[] bytes)
    {
        var started = (await (await staff.PostApiAsync($"/api/horses/{horseId}/uploads", new { fileName = name, contentType = type, length = bytes.Length })).Content.ReadFromJsonAsync<UploadStarted>())!;
        for (var n = 0; n < started.Parts; n++)
            Assert.Equal(HttpStatusCode.NoContent, (await PutPartAsync(staff, started.Id, n, bytes.Skip(n * started.PartBytes).Take(started.PartBytes).ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/uploads/{started.Id}/complete", new { })).StatusCode);
        return started.Id;
    }

    [Fact]
    public async Task A_video_comes_up_in_pieces_and_only_whole_and_only_for_whoever_started_it()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        using var other = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        using var elsewhere = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        var bytes = Enumerable.Range(0, MediaUpload.PartBytes + 1000).Select(i => (byte)(i % 253)).ToArray();
        var path = $"/api/horses/{seed.HorseId}/uploads";

        // Media only, not too big, not empty, by staff who can post, on this business's horse.
        object Start(string name, string type, long length) => new { fileName = name, contentType = type, length };
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Start("page.html", "text/html", 10))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Start("gallop.mp4", "video/mp4", MediaAsset.MaxBytes + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Start("gallop.mp4", "video/mp4", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync(path, Start("gallop.mp4", "video/mp4", 10))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.PostApiAsync(path, Start("gallop.mp4", "video/mp4", 10))).StatusCode);

        var started = (await (await staff.PostApiAsync(path, Start("gallop.mp4", "video/mp4", bytes.Length))).Content.ReadFromJsonAsync<UploadStarted>())!;
        Assert.Equal((MediaUpload.PartBytes, 2), (started.PartBytes, started.Parts));
        var (first, second) = (bytes[..MediaUpload.PartBytes], bytes[MediaUpload.PartBytes..]);

        // Nobody else's to add to; not a piece it hasn't got; not a piece cut short or too long; not whole until it is.
        Assert.Equal(HttpStatusCode.NotFound, (await PutPartAsync(other, started.Id, 0, first)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutPartAsync(elsewhere, started.Id, 0, first)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPartAsync(staff, started.Id, 2, second)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPartAsync(staff, started.Id, 0, first[..500])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPartAsync(staff, started.Id, 1, [.. second, 1])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutPartAsync(staff, started.Id, 1, second)).StatusCode); // any order
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync($"/api/uploads/{started.Id}/complete", new { })).StatusCode);

        // Not finished, it can't be sent; then the rest arrives (twice, after a dropped signal), it's finished (twice), and it can.
        object Update(Guid key, Guid upload) => new { key, eventId = (Guid?)seed.EventId, scope = "Owners", body = "Her gallop this morning.", uploadIds = new[] { upload } };
        var send = $"/api/horses/{seed.HorseId}/updates";
        var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(send, Update(key, started.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutPartAsync(staff, started.Id, 0, first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutPartAsync(staff, started.Id, 0, first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/uploads/{started.Id}/complete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/uploads/{started.Id}/complete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostApiAsync(send, Update(Guid.NewGuid(), started.Id))).StatusCode); // not theirs to send

        var sent = (await (await staff.PostApiAsync(send, Update(key, started.Id))).Content.ReadFromJsonAsync<UpdateSent>())!;
        Assert.Equal((seed.EventId, 2), (sent.EventId, sent.Recipients));

        // Sent again after the answer was lost: the same one. A different update can't have the same file.
        var again = await staff.PostApiAsync(send, Update(key, started.Id));
        Assert.Equal(sent, await again.Content.ReadFromJsonAsync<UpdateSent>());
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(send, Update(Guid.NewGuid(), started.Id))).StatusCode);
        await using (var context = db.ContextFor(seed.TenantId)) Assert.Equal(1, await context.StreamItems.CountAsync(i => i.Body == "Her gallop this morning."));

        // It's whole, and Ann can watch it.
        using var ann = await SignInAsync(seed.AnnEmail);
        Guid asset;
        await using (var context = db.ContextFor(seed.TenantId)) asset = (await context.MediaAssets.SingleAsync(a => a.StreamItemId == sent.ItemId)).Id;
        Assert.Equal(bytes, await ann.GetByteArrayAsync($"/api/media/{asset}"));
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync($"/api/uploads/{started.Id}/remove", new { })).StatusCode);

        // Taken off before sending: gone, and it can't be used.
        var unwanted = await UploadedAsync(staff, seed.HorseId, "wrong.jpg", "image/jpeg", Jpeg);
        Assert.Equal(HttpStatusCode.NoContent, (await other.PostApiAsync($"/api/uploads/{unwanted}/remove", new { })).StatusCode); // not theirs: nothing happens
        await using (var context = db.ContextFor(seed.TenantId)) Assert.True(await context.MediaUploads.AnyAsync(u => u.Id == unwanted));
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/uploads/{unwanted}/remove", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(send, Update(Guid.NewGuid(), unwanted))).StatusCode);
    }

    [Fact]
    public async Task An_update_about_nothing_in_particular_goes_on_the_standing_event_which_stays_out_of_the_way()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var path = $"/api/horses/{seed.HorseId}/updates";
        var options = (await staff.GetFromJsonAsync<UpdateChoices>($"{path}/options"))!;
        Assert.Equal((true, 2), (options.Managed, options.Owners.Count));
        Assert.Contains(options.Events, e => e.Id == seed.EventId);
        Assert.Null(options.Events.Single(e => e.Title == "General updates").Id); // always offered, even before there's been one

        // Who it would go to, before there is one; asking makes nothing.
        var who = await staff.PostApiAsync($"{path}/recipients", new { eventId = (Guid?)null, scope = "Owners", media = true });
        Assert.Equal(2, (await who.Content.ReadFromJsonAsync<RecipientsPreview>())!.Going.Count);
        await using (var context = db.ContextFor(seed.TenantId)) Assert.False(await context.Events.AnyAsync(e => e.Standing));

        // Refused ones leave nothing behind either.
        object Update(string? body, string scope = "Owners", Guid[]? uploads = null, Guid[]? named = null, Guid? key = null) =>
            new { key = key ?? Guid.NewGuid(), eventId = (Guid?)null, scope, body, uploadIds = uploads ?? [], namedParties = named ?? [] };
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Update(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Update("Hi", "Trainer"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Update("Hi", "NamedParties"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(path, Update("Hi", key: Guid.Empty))).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync(path, Update("Hi"))).StatusCode);
        await using (var context = db.ContextFor(seed.TenantId)) Assert.False(await context.Events.AnyAsync(e => e.Standing));

        // Voice, video, a photo and words, all on one update; the next one is on the same event.
        var files = new[]
        {
            await UploadedAsync(staff, seed.HorseId, "voice.m4a", "audio/mp4", [0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70]),
            await UploadedAsync(staff, seed.HorseId, "gallop.mov", "video/quicktime", [0, 0, 0, 0x14, 0x66, 0x74, 0x79, 0x70]),
            await UploadedAsync(staff, seed.HorseId, "photo.jpg", "image/jpeg", Jpeg),
        };
        var first = (await (await staff.PostApiAsync(path, Update("All well.", uploads: files))).Content.ReadFromJsonAsync<UpdateSent>())!;
        var second = (await (await staff.PostApiAsync(path, Update("Still well."))).Content.ReadFromJsonAsync<UpdateSent>())!;
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(2, first.Recipients);
        await using (var context = db.ContextFor(seed.TenantId))
        {
            Assert.Equal([MediaKind.Audio, MediaKind.Video, MediaKind.Photo], (await context.MediaAssets.Where(a => a.StreamItemId == first.ItemId).OrderBy(a => a.CreatedAt).ToListAsync()).Select(a => a.Kind));
            Assert.Equal(1, await context.Events.CountAsync(e => e.Standing));
        }

        // Staff only, and to one owner: only they get it, and only they can open it.
        Assert.Equal(0, (await (await staff.PostApiAsync(path, Update("Between us.", "Internal"))).Content.ReadFromJsonAsync<UpdateSent>())!.Recipients);
        var annsPhoto = await UploadedAsync(staff, seed.HorseId, "ann.jpg", "image/jpeg", Jpeg);
        var toAnn = (await (await staff.PostApiAsync(path, Update("For Ann.", "NamedParties", [annsPhoto], [seed.AnnId]))).Content.ReadFromJsonAsync<UpdateSent>())!;
        Assert.Equal(1, toAnn.Recipients);
        Guid asset;
        await using (var context = db.ContextFor(seed.TenantId)) asset = (await context.MediaAssets.SingleAsync(a => a.StreamItemId == toAnn.ItemId)).Id;
        using var ann = await SignInAsync(seed.AnnEmail);
        using var bob = await SignInAsync(BobEmail(seed));
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync($"/api/media/{asset}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/media/{asset}")).StatusCode);

        // Owners see it as one of the horse's events. Mail to the horse still files itself on the race, not on this.
        Assert.Contains("General updates", await ann.GetStringAsync($"/api/my/horses/{seed.HorseId}/events"));
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var slug = (await context.Tenants.SingleAsync()).Slug;
            var inbox = (await context.RoutingAddresses.SingleAsync(r => r.Kind == RoutingAddressKind.HorseInbox && r.HorseId == seed.HorseId)).Token;
            await InboundEmailAsync($"{inbox}@{slug}.in.paddockside.com.au", "trainer@stable.test", "Barrier", "Drawn 4.");
            var filed = await context.StreamItems.SingleAsync(i => i.Body == "Drawn 4.");
            Assert.NotEqual(first.EventId, filed.EventId);
        }

        // Another business: no such horse.
        using var elsewhere = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.GetAsync($"{path}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.PostApiAsync(path, Update("Hi"))).StatusCode);
    }

    private sealed record RecipientsPreview(List<NewHorse> Going);
}
