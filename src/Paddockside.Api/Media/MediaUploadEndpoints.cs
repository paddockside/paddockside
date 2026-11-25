using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Media;

/// <summary>
/// A phone's file coming up in pieces (<see cref="MediaUpload"/>): start it, put each piece (again, if the signal dropped),
/// then finish it. Only the person who started it can add to it, it has to come to exactly the length it said, and it's
/// nobody's to see until the update it's on is sent.
/// </summary>
public static class MediaUploadEndpoints
{
    public sealed record StartRequest(string? FileName, string? ContentType, long Length);

    public sealed record Started(Guid Id, int PartBytes, int Parts);

    private static readonly string[] CanPost = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapMediaUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/horses/{id:guid}/uploads", Start).RequireAuthorization(StaffPolicy.Name);
        var uploads = app.MapGroup("/api/uploads/{id:guid}").RequireAuthorization(StaffPolicy.Name);
        uploads.MapPut("/parts/{part:int}", Part).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MediaUpload.PartBytes + 1024));
        uploads.MapPost("/complete", Complete);
        uploads.MapPost("/remove", Remove);
    }

    private static Guid PersonOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<IResult> Start(Guid id, StartRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read updates but not add to them.");
        var horse = await db.Horses.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        var name = string.IsNullOrWhiteSpace(request.FileName) ? "media" : request.FileName;
        MediaUpload upload;
        try
        {
            upload = new MediaUpload(horse, PersonOf(user), name, MediaAsset.TypeFor(request.ContentType, name) ?? request.ContentType ?? "", request.Length, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.MediaUploads.Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/uploads/{upload.Id}", new Started(upload.Id, MediaUpload.PartBytes, upload.Parts));
    }

    /// <summary>One piece, as raw bytes. Putting it again replaces it.</summary>
    private static async Task<IResult> Part(Guid id, int part, HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, CancellationToken cancellationToken)
    {
        if (await MineAsync(id, user, db, cancellationToken) is not { } upload) return Results.NotFound();
        if (upload.CompletedAt is not null) return Results.NoContent();
        if (part < 0 || part >= upload.Parts) return Problem("That isn't a piece of this file.");

        // Held whole (4 MB at most), so a piece cut off part way is never kept.
        var expected = (int)upload.PartLength(part);
        var bytes = new byte[expected];
        var read = await request.Body.ReadAtLeastAsync(bytes, expected, throwOnEndOfStream: false, cancellationToken);
        if (read != expected || await request.Body.ReadAsync(new byte[1], cancellationToken) != 0) return Problem("That piece didn't all arrive. Send it again.");
        using var content = new MemoryStream(bytes);
        await store.SavePartAsync(upload.BlobName, part, content, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Complete(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await MineAsync(id, user, db, cancellationToken) is not { } upload) return Results.NotFound();
        if (upload.CompletedAt is not null) return Results.NoContent();
        if (!await store.JoinPartsAsync(upload.BlobName, upload.Parts, upload.Length, upload.ContentType, cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Some of that file hasn't arrived yet.");
        upload.Complete(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Taken off before it was sent. One already on an update stays: the update has it.</summary>
    private static async Task<IResult> Remove(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, CancellationToken cancellationToken)
    {
        if (await MineAsync(id, user, db, cancellationToken) is not { } upload) return Results.NoContent();
        if (upload.StreamItemId is not null) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That's already on an update.");
        db.MediaUploads.Remove(upload);
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteAsync(upload.BlobName, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<MediaUpload?> MineAsync(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var person = PersonOf(user);
        return await db.MediaUploads.SingleOrDefaultAsync(u => u.Id == id && u.PersonId == person, cancellationToken);
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
