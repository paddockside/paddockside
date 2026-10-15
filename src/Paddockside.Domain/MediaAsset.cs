namespace Paddockside.Domain;

public enum MediaKind
{
    Photo,
    Video,
    Audio,
}

/// <summary>
/// A photo, video or voice note (data-model.md MEDIA_ASSET; D14: media is first-class, not an attachment). Belongs to
/// the stream item that introduced it, so it is seen by exactly who may see that item; it also carries its horse, so
/// a horse's gallery can be read without walking the stream. The original is kept as uploaded, in the tenant's
/// private media container; it is only ever served through the app, after the access check.
/// </summary>
public sealed class MediaAsset
{
    /// <summary>The largest single file staff can upload: a few minutes of phone video.</summary>
    public const long MaxBytes = 250L * 1024 * 1024;

    /// <summary>
    /// What can be uploaded, by file type, and what kind of media each is. Only these are ever stored or served, so a
    /// file can never come back as a page or a script.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (MediaKind Kind, string Extension)> Types = new Dictionary<string, (MediaKind, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = (MediaKind.Photo, ".jpg"),
        ["image/png"] = (MediaKind.Photo, ".png"),
        ["image/webp"] = (MediaKind.Photo, ".webp"),
        ["image/gif"] = (MediaKind.Photo, ".gif"),
        ["image/heic"] = (MediaKind.Photo, ".heic"),
        ["video/mp4"] = (MediaKind.Video, ".mp4"),
        ["video/quicktime"] = (MediaKind.Video, ".mov"),
        ["video/webm"] = (MediaKind.Video, ".webm"),
        ["audio/mpeg"] = (MediaKind.Audio, ".mp3"),
        ["audio/mp4"] = (MediaKind.Audio, ".m4a"),
        ["audio/x-m4a"] = (MediaKind.Audio, ".m4a"),
        ["audio/aac"] = (MediaKind.Audio, ".aac"),
        ["audio/wav"] = (MediaKind.Audio, ".wav"),
        ["audio/x-wav"] = (MediaKind.Audio, ".wav"),
        ["audio/webm"] = (MediaKind.Audio, ".webm"),
        ["audio/ogg"] = (MediaKind.Audio, ".ogg"),
    };

    /// <summary>Phones and mail programs often send no type, or a generic one; the extension says what it is.</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".heic"] = "image/heic", [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
    };

    /// <summary>The type to keep a file as: its own if we know it, else from its extension; null if it isn't media.</summary>
    public static string? TypeFor(string? contentType, string fileName) =>
        contentType is not null && Types.ContainsKey(contentType) ? contentType.ToLowerInvariant()
        : ByExtension.GetValueOrDefault(Path.GetExtension(fileName));

    /// <summary>For EF Core materialisation.</summary>
    private MediaAsset() => (FileName, ContentType, BlobName) = (null!, null!, null!);

    /// <summary>
    /// A new file on <paramref name="item"/>: a media item staff add, or an email that came in with photos or video
    /// attached (staff only, like the email, until staff share it).
    /// </summary>
    public MediaAsset(StreamItem item, string fileName, string contentType, long length, DateTimeOffset at)
    {
        if (item.Kind != StreamItemKind.Media && item is not { Kind: StreamItemKind.Message, Direction: StreamItemDirection.Inbound })
            throw new DomainException("Media belongs to a media item, or to an email that brought it.");
        if (!Types.TryGetValue(contentType, out var type)) throw new DomainException($"{fileName} isn't a photo, video or voice note we can keep.");
        if (length <= 0) throw new DomainException($"{fileName} is empty.");
        if (length > MaxBytes) throw new DomainException($"{fileName} is too big: the most is {MaxBytes / 1024 / 1024} MB.");

        TenantId = item.TenantId;
        StreamItemId = item.Id;
        HorseId = item.HorseId;
        Kind = type.Kind;
        FileName = Path.GetFileName(fileName.Trim()) is { Length: > 0 } name ? name[..Math.Min(name.Length, 200)] : $"media{type.Extension}";
        ContentType = contentType.ToLowerInvariant();
        Length = length;
        CreatedAt = at;
        BlobName = $"{TenantId}/{HorseId}/{Id}{type.Extension}";
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid StreamItemId { get; }

    public Guid HorseId { get; }

    public MediaKind Kind { get; }

    /// <summary>The name it was uploaded with, for download; never used as a path.</summary>
    public string FileName { get; }

    public string ContentType { get; }

    public long Length { get; }

    /// <summary>Where the original is kept in the media container: tenant / horse / asset.</summary>
    public string BlobName { get; private init; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The asset this was shared from: an emailed photo staff shared with owners. The same stored file.</summary>
    public Guid? SharedFromId { get; private set; }

    /// <summary>The same file on a media item staff share with owners; nothing is copied in storage.</summary>
    public MediaAsset ShareOn(StreamItem mediaItem, DateTimeOffset at)
    {
        if (mediaItem.Kind != StreamItemKind.Media) throw new DomainException("Shared media goes on a media item.");
        if (mediaItem.HorseId != HorseId) throw new DomainException("Shared media stays on its own horse.");
        return new MediaAsset(mediaItem, FileName, ContentType, Length, at) { SharedFromId = Id, BlobName = BlobName };
    }
}
