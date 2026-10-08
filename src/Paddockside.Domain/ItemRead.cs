namespace Paddockside.Domain;

/// <summary>
/// One party has opened one stream item (messaging-channels.md §5: "unread state is per person per item"). Drives
/// the owner's unread counts and, later, the staff "who hasn't seen it" view. Written once, when the item is first
/// shown to them; never removed.
/// </summary>
public sealed class ItemRead
{
    /// <summary>For EF Core materialisation.</summary>
    private ItemRead() { }

    public ItemRead(StreamItem item, Guid partyId, DateTimeOffset at)
    {
        TenantId = item.TenantId;
        StreamItemId = item.Id;
        PartyId = partyId;
        ReadAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid StreamItemId { get; }

    public Guid PartyId { get; }

    public DateTimeOffset ReadAt { get; }
}
