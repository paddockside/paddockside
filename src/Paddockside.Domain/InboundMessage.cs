namespace Paddockside.Domain;

/// <summary>
/// Where an inbound message is up to (messaging-channels.md §3.2). Tier 3 scoring will add Suggested; until then
/// anything not placed by an address goes to Pending for staff.
/// </summary>
public enum InboundState
{
    /// <summary>Stored exactly as it arrived; not processed yet.</summary>
    Received,

    /// <summary>Became a stream item.</summary>
    Placed,

    /// <summary>Could not be placed by its address; waiting for staff.</summary>
    Pending,

    /// <summary>An auto-reply, out-of-office or bounce: never placed, never answered.</summary>
    Ignored,

    /// <summary>Failed a safety check (the provider's spam verdict). Tenant admins only.</summary>
    Held,
}

/// <summary>
/// The raw arrival, kept separate from the stream item it becomes (data-model.md INBOUND_MESSAGE). The original is
/// preserved as it arrived: the provider's payload is stored verbatim in blob storage
/// (<see cref="RawBlobName"/>), with headers and both bodies here, so matching can be re-run and nothing is lost to
/// the display clean-up.
/// </summary>
public sealed class InboundMessage
{
    private readonly List<InboundAttachment> _attachments = [];

    /// <summary>For EF Core materialisation.</summary>
    private InboundMessage() =>
        (Channel, Provider, ProviderMessageId, FromAddress, RecipientAddress, Recipients, Headers, RawBlobName) =
        (null!, null!, null!, null!, null!, null!, null!, null!);

    public InboundMessage(
        Guid tenantId,
        string provider,
        string providerMessageId,
        DateTimeOffset receivedAt,
        string fromAddress,
        string? fromName,
        string recipientAddress,
        string recipients,
        string? subject,
        string? textBody,
        string? htmlBody,
        string headers,
        string rawBlobName)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId)) throw new DomainException("An inbound message needs the provider's message id.");
        TenantId = tenantId;
        Channel = "Email";
        Provider = provider;
        ProviderMessageId = providerMessageId;
        ReceivedAt = receivedAt;
        StateAt = receivedAt;
        FromAddress = fromAddress.Trim().ToLowerInvariant();
        FromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim();
        RecipientAddress = recipientAddress.Trim().ToLowerInvariant();
        Recipients = recipients;
        Subject = subject;
        TextBody = textBody;
        HtmlBody = htmlBody;
        Headers = headers;
        RawBlobName = rawBlobName;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Channel { get; }

    public string Provider { get; }

    /// <summary>The provider's id; a webhook delivered twice is stored once.</summary>
    public string ProviderMessageId { get; }

    public DateTimeOffset ReceivedAt { get; }

    public string FromAddress { get; }

    public string? FromName { get; }

    /// <summary>The address on our inbound domain it was matched by (or first arrived at).</summary>
    public string RecipientAddress { get; }

    /// <summary>Every To and Cc address, as received.</summary>
    public string Recipients { get; }

    public string? Subject { get; }

    /// <summary>The full plain-text body, quoted history and all.</summary>
    public string? TextBody { get; }

    public string? HtmlBody { get; }

    /// <summary>Every header, as a JSON array of name/value pairs in arrival order.</summary>
    public string Headers { get; }

    /// <summary>The provider's payload, byte for byte, in blob storage.</summary>
    public string RawBlobName { get; }

    public IReadOnlyList<InboundAttachment> Attachments => _attachments;

    public InboundState State { get; private set; } = InboundState.Received;

    public DateTimeOffset StateAt { get; private set; }

    /// <summary>Why it is where it is, in words staff can read ("Reply token", "No open event in its window").</summary>
    public string? Reason { get; private set; }

    /// <summary>1: a recipient's reply token; 2: a horse's own address. Null when not placed by an address.</summary>
    public int? MatchTier { get; private set; }

    public Guid? RoutingAddressId { get; private set; }

    public Guid? PartyId { get; private set; }

    public Guid? HorseId { get; private set; }

    public Guid? EventId { get; private set; }

    public Guid? StreamItemId { get; private set; }

    /// <summary>The body as shown: quoted history and signatures removed.</summary>
    public string? DisplayBody { get; private set; }

    public void AddAttachment(string fileName, string contentType, long length, string? contentId, string? blobName, string? droppedReason)
    {
        if (State != InboundState.Received) throw new DomainException("Attachments are recorded on arrival.");
        _attachments.Add(new InboundAttachment(fileName, contentType, length, contentId, blobName, droppedReason));
    }

    /// <summary>Became <paramref name="item"/>. Never re-placed once placed: moving it is a stream operation.</summary>
    public void Place(int tier, StreamItem item, Guid? routingAddressId, Guid? partyId, string displayBody, string reason, DateTimeOffset at)
    {
        if (State is not (InboundState.Received or InboundState.Pending)) throw new DomainException($"A {State} message cannot be placed.");
        if (item.TenantId != TenantId) throw new DomainException("An inbound message is placed in its own tenant.");
        MatchTier = tier;
        StreamItemId = item.Id;
        HorseId = item.HorseId;
        EventId = item.EventId;
        RoutingAddressId = routingAddressId;
        PartyId = partyId;
        DisplayBody = displayBody;
        Move(InboundState.Placed, reason, at);
    }

    public void Park(string reason, string displayBody, Guid? partyId, DateTimeOffset at)
    {
        DisplayBody = displayBody;
        PartyId = partyId;
        Move(InboundState.Pending, reason, at);
    }

    public void Ignore(string reason, DateTimeOffset at) => Move(InboundState.Ignored, reason, at);

    public void Hold(string reason, DateTimeOffset at) => Move(InboundState.Held, reason, at);

    private void Move(InboundState to, string reason, DateTimeOffset at)
    {
        if (to != InboundState.Placed && State != InboundState.Received) throw new DomainException($"A {State} message has already been processed.");
        State = to;
        StateAt = at;
        Reason = reason;
    }
}

/// <summary>
/// One attachment as it arrived. Kept ones are in blob storage (<see cref="BlobName"/>) as media candidates; dropped
/// ones (executables, signature logos) are recorded with the reason, so nothing disappears silently.
/// </summary>
public sealed class InboundAttachment
{
    /// <summary>For EF Core materialisation.</summary>
    private InboundAttachment() => (FileName, ContentType) = (null!, null!);

    internal InboundAttachment(string fileName, string contentType, long length, string? contentId, string? blobName, string? droppedReason)
    {
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        ContentId = contentId;
        BlobName = blobName;
        DroppedReason = droppedReason;
    }

    public string FileName { get; }

    public string ContentType { get; }

    public long Length { get; }

    /// <summary>Set for inline parts (images referenced from the HTML body).</summary>
    public string? ContentId { get; }

    public string? BlobName { get; }

    public string? DroppedReason { get; }
}
