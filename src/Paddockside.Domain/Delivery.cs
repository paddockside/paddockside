namespace Paddockside.Domain;

public enum DeliveryChannel
{
    Email,
    Sms,
    Portal,
}

public enum DeliveryStatus
{
    Queued,
    Sent,
    Delivered,
    Opened,
    Replied,
    Bounced,
    Suppressed,
}

/// <summary>
/// One recipient of one outbound message, on one channel (data-model.md §2 DELIVERY): the audit trail of who was
/// told, when, and whether they opened it. Created at send time, never ahead, so a party who exited this
/// morning is never on tonight's list.
/// </summary>
public sealed class Delivery
{
    /// <summary>For EF Core materialisation.</summary>
    private Delivery() { }

    internal Delivery(StreamItem message, Guid partyId, DeliveryChannel channel, DateTimeOffset at)
    {
        TenantId = message.TenantId;
        StreamItemId = message.Id;
        PartyId = partyId;
        Channel = channel;
        Status = DeliveryStatus.Queued;
        StatusAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid StreamItemId { get; }

    public Guid PartyId { get; }

    public DeliveryChannel Channel { get; }

    public DeliveryStatus Status { get; private set; }

    public DateTimeOffset StatusAt { get; private set; }

    /// <summary>The address actually used (data-model.md DELIVERY: "address used").</summary>
    public string? Address { get; private set; }

    /// <summary>The routing token issued to this recipient with this message.</summary>
    public Guid? RoutingAddressId { get; private set; }

    /// <summary>The provider's id for the sent message (Postmark MessageID), to match its webhooks.</summary>
    public string? ProviderMessageId { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Why it was not sent, or the last error, in words for staff.</summary>
    public string? Note { get; private set; }

    /// <summary>Retried until this many attempts, then left for staff to see.</summary>
    public const int MaxAttempts = 5;

    public bool IsWaitingToSend => Status == DeliveryStatus.Queued && Attempts < MaxAttempts;

    public void AssignRoutingAddress(RoutingAddress address)
    {
        if (address.TenantId != TenantId || address.StreamItemId != StreamItemId || address.PartyId != PartyId)
            throw new DomainException("The routing address was issued for a different recipient or message.");
        RoutingAddressId = address.Id;
    }

    public void MarkSent(string address, string providerMessageId, DateTimeOffset at)
    {
        if (Status != DeliveryStatus.Queued) throw new DomainException("Only a queued delivery is sent.");
        Address = address;
        ProviderMessageId = providerMessageId;
        SentAt = at;
        Attempts++;
        Note = null;
        Record(DeliveryStatus.Sent, at);
    }

    /// <summary>Not sent, on purpose: no address, an undeliverable address, or no longer an owner.</summary>
    public void Suppress(string reason, DateTimeOffset at, string? address = null)
    {
        Address ??= address;
        Note = reason;
        Record(DeliveryStatus.Suppressed, at);
    }

    /// <summary>The provider reported a permanent bounce after sending.</summary>
    public void RecordBounce(string description, DateTimeOffset at)
    {
        Note = description;
        Record(DeliveryStatus.Bounced, at);
    }

    /// <summary>A failed attempt. Transient failures stay queued for another try; permanent ones bounce.</summary>
    public void RecordFailedAttempt(string error, bool permanent, DateTimeOffset at, string? address = null)
    {
        Address ??= address;
        Attempts++;
        Note = error;
        if (permanent) Record(DeliveryStatus.Bounced, at);
        else StatusAt = at;
    }

    /// <summary>Records what the channel provider reported. Statuses only move forward, except to a failure.</summary>
    public void Record(DeliveryStatus status, DateTimeOffset at)
    {
        var failure = status is DeliveryStatus.Bounced or DeliveryStatus.Suppressed;
        if (!failure && status < Status) return;
        Status = status;
        StatusAt = at;
    }

    /// <summary>
    /// The deliveries for a message, one per party the access rule admits right now
    /// (<see cref="AccessRule.CurrentOwnerAudience"/>).
    /// </summary>
    public static IReadOnlyList<Delivery> ForOwners(Tenant tenant, Horse horse, StreamItem message, DeliveryChannel channel, DateTimeOffset at)
    {
        if (message.Kind != StreamItemKind.Message || message.Direction != StreamItemDirection.Outbound)
            throw new DomainException("Only an outbound message is delivered.");

        return AccessRule.CurrentOwnerAudience(tenant, horse, message)
            .Select(partyId => new Delivery(message, partyId, channel, at))
            .ToList();
    }
}
