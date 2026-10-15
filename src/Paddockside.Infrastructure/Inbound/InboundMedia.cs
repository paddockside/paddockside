using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>
/// An email's photos, video and voice notes, once it is placed (messaging-channels.md §3.3: "every attachment becomes
/// a MEDIA_ASSET candidate"). The originals stay in the inbound container as they arrived; a copy goes into the media
/// container as an asset on the email's item, staff only like the email itself, until staff share it with owners.
/// Attachments that were dropped (signature logos, executables) and anything that isn't media are left alone.
/// </summary>
public static class InboundMedia
{
    public static async Task<int> AttachAsync(PaddocksideDbContext db, IInboundStore inbound, IMediaStore media, InboundMessage message, StreamItem item,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var added = 0;
        foreach (var attachment in message.Attachments.Where(a => a.DroppedReason is null && a.BlobName is not null))
        {
            if (MediaAsset.TypeFor(attachment.ContentType, attachment.FileName) is not { } type || attachment.Length > MediaAsset.MaxBytes) continue;
            if (await inbound.ReadAsync(attachment.BlobName!, cancellationToken) is not { Length: > 0 } bytes) continue;

            var asset = new MediaAsset(item, attachment.FileName, type, bytes.Length, now.AddMilliseconds(added)); // in the order they were attached
            using var content = new MemoryStream(bytes, writable: false);
            await media.SaveAsync(asset.BlobName, content, asset.ContentType, cancellationToken);
            db.MediaAssets.Add(asset);
            added++;
        }

        return added;
    }
}
