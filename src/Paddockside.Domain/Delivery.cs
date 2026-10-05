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
