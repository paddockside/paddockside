namespace Paddockside.Domain;

/// <summary>
/// A file on its way up from a member of staff's phone, in pieces (David, 25 Nov 2026), so a dropped signal costs one piece
/// and not the whole video. It starts before the update is sent, and becomes a <see cref="MediaAsset"/> when it is; until
/// then nobody can see it.
/// </summary>
public sealed class MediaUpload
{
    /// <summary>One piece. Small enough to repeat on a poor signal; every piece but the last is exactly this.</summary>
    public const int PartBytes = 4 * 1024 * 1024;

    /// <summary>For EF Core materialisation.</summary>
    private MediaUpload() => (FileName, ContentType, BlobName) = (null!, null!, null!);

    public MediaUpload(Horse horse, Guid personId, string fileName, string contentType, long length, DateTimeOffset at)
    {
        if (!MediaAsset.Types.TryGetValue(contentType, out var type)) throw new DomainException($"{fileName} isn't a photo, video or voice note we can keep.");
        if (length <= 0) throw new DomainException($"{fileName} is empty.");
        if (length > MediaAsset.MaxBytes) throw new DomainException($"{fileName} is too big: the most is {MediaAsset.MaxBytes / 1024 / 1024} MB.");
        TenantId = horse.TenantId;
        HorseId = horse.Id;
        PersonId = personId;
        FileName = Path.GetFileName(fileName.Trim()) is { Length: > 0 } name ? name[..Math.Min(name.Length, 200)] : $"media{type.Extension}";
        ContentType = contentType.ToLowerInvariant();
        Length = length;
        CreatedAt = at;
        BlobName = $"{TenantId}/{HorseId}/{Id}{type.Extension}";
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    /// <summary>The member of staff sending it: only they can add to it or use it.</summary>
    public Guid PersonId { get; }

    public string FileName { get; }

    public string ContentType { get; }

    public long Length { get; }

    public string BlobName { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>When all of it had arrived.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The update it went on.</summary>
    public Guid? StreamItemId { get; private set; }

    public int Parts => (int)((Length + PartBytes - 1) / PartBytes);

    /// <summary>How long piece <paramref name="part"/> (from 0) must be.</summary>
    public long PartLength(int part) => part == Parts - 1 ? Length - (long)part * PartBytes : PartBytes;

    public void Complete(DateTimeOffset at) => CompletedAt ??= at;

    public void UseOn(StreamItem item)
    {
        if (CompletedAt is null) throw new DomainException($"{FileName} hasn't finished uploading.");
        if (StreamItemId is not null) throw new DomainException($"{FileName} is already on an update.");
        if (item.HorseId != HorseId) throw new DomainException($"{FileName} was added for another horse.");
        StreamItemId = item.Id;
    }
}

/// <summary>
/// An update sent from a phone, by the key the phone made for it: sent again after a dropped signal, it's the same update,
/// not a second one.
/// </summary>
public sealed class SentUpdate
{
    /// <summary>For EF Core materialisation.</summary>
    private SentUpdate() { }

    public SentUpdate(StreamItem item, Guid key, Guid personId, int recipients, DateTimeOffset at) =>
        (TenantId, StreamItemId, Key, PersonId, Recipients, CreatedAt) = (item.TenantId, item.Id, key, personId, recipients, at);

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid Key { get; }

    public Guid PersonId { get; }

    public Guid StreamItemId { get; }

    public int Recipients { get; }

    public DateTimeOffset CreatedAt { get; }
}
