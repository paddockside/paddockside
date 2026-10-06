namespace Paddockside.Domain;

public enum OwnerInvitationStatus
{
    Queued,
    Sent,

    /// <summary>Nothing was sent: no email address, the party already signs in, or the address bounced.</summary>
    Skipped,
}

/// <summary>
/// "You are now an owner": queued automatically when a party first gains a managed interest, if the tenant's
/// "invite owners on first interest" setting is on (identity-access.md §6, default on). The email carries a 14-day
/// single-use link that signs them in. Owners need never accept it: they receive everything by email or SMS as a
/// party either way, and the first link they tap from any notification does the same job.
/// </summary>
public sealed class OwnerInvitation
{
    /// <summary>For EF Core materialisation.</summary>
    private OwnerInvitation()
    {
    }

    public OwnerInvitation(Guid tenantId, Guid partyId, Guid horseId, DateTimeOffset at)
    {
        TenantId = tenantId;
        PartyId = partyId;
        HorseId = horseId;
        CreatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid PartyId { get; }

    /// <summary>The horse the first interest is in: the invitation names it.</summary>
    public Guid HorseId { get; }

    public DateTimeOffset CreatedAt { get; }

    public OwnerInvitationStatus Status { get; private set; } = OwnerInvitationStatus.Queued;

    public DateTimeOffset? SentAt { get; private set; }

    public string? Address { get; private set; }

    public string? Note { get; private set; }

    public void MarkSent(string address, DateTimeOffset at)
    {
        if (Status != OwnerInvitationStatus.Queued) throw new DomainException("Only a queued invitation is sent.");
        (Status, Address, SentAt) = (OwnerInvitationStatus.Sent, address, at);
    }

    public void Skip(string reason, DateTimeOffset at)
    {
        if (Status != OwnerInvitationStatus.Queued) throw new DomainException("Only a queued invitation is skipped.");
        (Status, Note, SentAt) = (OwnerInvitationStatus.Skipped, reason, at);
    }
}
