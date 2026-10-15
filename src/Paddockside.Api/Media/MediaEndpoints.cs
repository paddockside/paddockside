using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Paddockside.Api.Audit;
using Paddockside.Api.Owners;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Media;

/// <summary>
/// Photos, video and voice notes staff add to an event (data-model.md MEDIA_ASSET; build plan, sprint 3), and the one
/// way any of it is read back: through the app, after the same access check as the item it belongs to. The media
/// container is private; there are no public or long-lived links (P22: media is linked, not attached, and the link
/// opens the portal).
/// </summary>
public static class MediaEndpoints
{
    public sealed record Uploaded(Guid Id, int Files, int Recipients);

    public sealed record ShareRequest(string? Caption);

    /// <summary>A handful of photos at once, or one video; more is a second post.</summary>
    public const int MaxFiles = 10;

    /// <summary>The whole upload: ten phone photos, or one long video with room to spare.</summary>
    public const long MaxRequestBytes = 600L * 1024 * 1024;

    private static readonly string[] CanPost = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events/{id:guid}/media", Upload).RequireAuthorization(StaffPolicy.Name)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBytes))
            .DisableAntiforgery(); // the API's own request header guards unsafe requests (RequireApiRequestHeader)
        app.MapGet("/api/media/{id:guid}", Get).RequireAuthorization();
        app.MapPost("/api/items/{id:guid}/share-media", Share).RequireAuthorization(StaffPolicy.Name);
    }

    /// <summary>
    /// A multipart form: <c>files</c>, an optional <c>caption</c>, and <c>scope</c> — "Owners" (shared with the
    /// horse's owners, who are told by their "new photos and videos" choice) or "Internal" (staff only).
    /// </summary>
    private static async Task<IResult> Upload(Guid id, HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store,
        AuditLog audit, TimeProvider clock, EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not add to them.");
        if (!request.HasFormContentType) return Problem("Choose the photos, video or voice note to add.");

        var form = await new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = MaxRequestBytes }).ReadFormAsync(cancellationToken);
        var files = form.Files.Where(f => f.Length > 0).ToList();
        if (files.Count == 0) return Problem("Choose the photos, video or voice note to add.");
        if (files.Count > MaxFiles) return Problem($"Add up to {MaxFiles} at a time.");

        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        var scope = form["scope"].ToString() switch
        {
            "Owners" => StreamItemScope.Owners,
            "Internal" => StreamItemScope.Internal,
            _ => (StreamItemScope?)null,
        };
        if (scope is null) return Problem("Choose who can see it: the owners, or staff only.");
        if (e.Status == EventStatus.Draft && scope != StreamItemScope.Internal)
            return Problem("This event is still a draft: owners can't see it. Open it first, or keep these for staff.");

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var author = user.FindFirstValue(ClaimTypes.Email) ?? "Staff";
        var caption = form["caption"].ToString();
        if (caption.Length > 4000) return Problem("That caption is too long.");

        string TypeOf(IFormFile file) => MediaAsset.TypeFor(file.ContentType, file.FileName) ?? file.ContentType ?? "";
        var kinds = files.Select(f => MediaAsset.Types.TryGetValue(TypeOf(f), out var t) ? t.Kind : MediaKind.Photo).ToList();

        StreamItem item;
        var assets = new List<(MediaAsset Asset, IFormFile File)>();
        try
        {
            item = StreamItem.Media(horse, e, Title(kinds), caption, scope.Value, now, author);
            // A millisecond apart, so they keep the order they were chosen in.
            foreach (var (file, n) in files.Select((f, n) => (f, n)))
                assets.Add((new MediaAsset(item, file.FileName, TypeOf(file), file.Length, now.AddMilliseconds(n)), file));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        // The files first: a database row never points at a file that isn't there.
        foreach (var (asset, file) in assets)
        {
            await using var content = file.OpenReadStream();
            await store.SaveAsync(asset.BlobName, content, asset.ContentType, cancellationToken);
        }

        db.StreamItems.Add(item);
        db.MediaAssets.AddRange(assets.Select(a => a.Asset));
        IReadOnlyList<Delivery> deliveries = scope == StreamItemScope.Internal ? []
            : await OutboundQueue.QueueAsync(db, tenant, horse, e, item, null, texts.IsConfigured, now, cancellationToken);
        e.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        if (deliveries.Count > 0) emailSignal.Notify();

        await audit.RecordAsync(tenant.Id, "media.added",
            $"Added {Describe(assets.Select(a => a.Asset).ToList())} to \"{e.Title}\" on {horse.Name} ({(scope == StreamItemScope.Internal ? "staff only" : "shared with owners")})",
            "StreamItem", item.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/events/{id}", new Uploaded(item.Id, assets.Count, deliveries.Count));
    }

    /// <summary>
    /// An email's photos and video, shared with the horse's owners: a new media item on the same event, with the
    /// caption staff write, holding the same stored files. The email itself stays staff only (it hasn't been checked
    /// for what owners may see); only its media goes out. Once only.
    /// </summary>
    private static async Task<IResult> Share(Guid id, ShareRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not share from them.");
        var source = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (source is null) return Results.NotFound();
        var assets = await db.MediaAssets.Where(a => a.StreamItemId == id).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        if (source is not { Kind: StreamItemKind.Message, Direction: StreamItemDirection.Inbound } || assets.Count == 0)
            return Problem("There are no photos or video on this to share.");
        if (source.EventId is not { } eventId) return Problem("Place it on an event first; owners see media on their horse's events.");
        var assetIds = assets.Select(a => a.Id).ToList();
        if (await db.MediaAssets.AnyAsync(a => a.SharedFromId != null && assetIds.Contains(a.SharedFromId.Value), cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "These are already shared with the owners.");
        if ((request.Caption?.Length ?? 0) > 4000) return Problem("That caption is too long.");

        var e = await db.Events.SingleAsync(x => x.Id == eventId, cancellationToken);
        if (e.Status == EventStatus.Draft) return Problem("This event is still a draft: owners can't see it. Open it first.");
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var item = StreamItem.Media(horse, e, Title(assets.Select(a => a.Kind).ToList()), request.Caption, StreamItemScope.Owners, now,
            user.FindFirstValue(ClaimTypes.Email) ?? "Staff");
        db.StreamItems.Add(item);
        db.MediaAssets.AddRange(assets.Select((a, n) => a.ShareOn(item, now.AddMilliseconds(n))));
        var deliveries = await OutboundQueue.QueueAsync(db, tenant, horse, e, item, null, texts.IsConfigured, now, cancellationToken);
        e.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        if (deliveries.Count > 0) emailSignal.Notify();

        await audit.RecordAsync(tenant.Id, "media.shared", $"Shared {Describe(assets)} from {source.AuthorName}'s email with the owners of {horse.Name} (\"{e.Title}\")",
            "StreamItem", item.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/events/{eventId}", new Uploaded(item.Id, assets.Count, deliveries.Count));
    }

    /// <summary>
    /// One file, to staff of the tenant, or to an owner who may see the item it belongs to (the access rule, as for
    /// every item). Seekable, so a video plays from any point; never sniffed, so it can only be the media it says.
    /// <c>?download=1</c> saves it under its original name.
    /// </summary>
    private static async Task<IResult> Get(Guid id, bool? download, HttpContext http, ClaimsPrincipal user, IAuthorizationService authorization,
        PaddocksideDbContext db, IMediaStore store, CancellationToken cancellationToken)
    {
        var asset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset is null) return Results.NotFound();

        if (!(await authorization.AuthorizeAsync(user, StaffPolicy.Name)).Succeeded)
        {
            if (!(await authorization.AuthorizeAsync(user, ClientPolicy.Name)).Succeeded
                || !Guid.TryParse(user.FindFirstValue(SessionClaims.Party), out var partyId))
                return Results.NotFound();
            var item = await db.StreamItems.AsNoTracking().SingleAsync(i => i.Id == asset.StreamItemId, cancellationToken);
            var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
                .SingleAsync(h => h.Id == asset.HorseId, cancellationToken);
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
            if (!AccessRule.CanSee(new Viewer(tenant.Id, AudienceClass.Client, partyId), tenant, horse, item)) return Results.NotFound();
        }

        var stream = await store.OpenReadAsync(asset.BlobName, cancellationToken);
        if (stream is null) return Results.NotFound();
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
        return Results.Stream(stream, asset.ContentType, download == true ? asset.FileName : null, enableRangeProcessing: true);
    }

    /// <summary>The item's title; the email's subject puts the horse first ("Faultless Miss: New photos").</summary>
    private static string Title(IReadOnlyList<MediaKind> kinds) => kinds.Distinct().ToList() switch
    {
        [MediaKind.Photo] => kinds.Count == 1 ? "A new photo" : "New photos",
        [MediaKind.Video] => kinds.Count == 1 ? "A new video" : "New videos",
        [MediaKind.Audio] => "A voice note",
        _ => "New photos and video",
    };

    private static string Describe(IReadOnlyList<MediaAsset> assets) => string.Join(", ", assets.GroupBy(a => a.Kind).Select(g => (g.Key, g.Count()) switch
    {
        (MediaKind.Photo, 1) => "1 photo",
        (MediaKind.Photo, var n) => $"{n} photos",
        (MediaKind.Video, 1) => "1 video",
        (MediaKind.Video, var n) => $"{n} videos",
        (_, 1) => "1 voice note",
        (_, var n) => $"{n} voice notes",
    }));

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
