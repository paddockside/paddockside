namespace Paddockside.Domain;

public enum SupportSessionStatus
{
    Requested,
    Declined,
    Active,
    Ended,
    Expired,
}

/// <summary>
/// A Paddockside operator's time-boxed look inside a tenant (identity-access.md §7): requested by the operator with a
/// reason and a duration (default 2 hours, at most 24), approved by one of the tenant's admins, ended early by
/// either side. While active the operator sees the tenant as a Viewer, and everything they view is in the tenant's
/// audit log.
/// </summary>
public sealed class SupportSession
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    /// <summary>For EF Core materialisation.</summary>
    private SupportSession() => (OperatorName, Reason) = (null!, null!);

    public SupportSession(Guid tenantId, Guid operatorPersonId, string operatorName, string reason, TimeSpan duration, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why you need access; the tenant admin reads it before approving.");
        if (duration <= TimeSpan.Zero || duration > MaxDuration) throw new DomainException("A support session lasts at most 24 hours.");
        TenantId = tenantId;
        OperatorPersonId = operatorPersonId;
        OperatorName = operatorName;
        Reason = reason.Trim();
        Duration = duration;
        RequestedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid OperatorPersonId { get; }

    public string OperatorName { get; }

    public string Reason { get; }

    public TimeSpan Duration { get; }

    public DateTimeOffset RequestedAt { get; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecidedBy { get; private set; }

    public bool Approved { get; private set; }

    /// <summary>When an approved session stops working: approval time plus the duration.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public string? EndedBy { get; private set; }

    public SupportSessionStatus StatusAt(DateTimeOffset now) => this switch
    {
        { DecidedAt: null } => SupportSessionStatus.Requested,
        { Approved: false } => SupportSessionStatus.Declined,
        { EndedAt: not null } => SupportSessionStatus.Ended,
        _ when ExpiresAt <= now => SupportSessionStatus.Expired,
        _ => SupportSessionStatus.Active,
    };

    public bool IsActive(DateTimeOffset now) => StatusAt(now) == SupportSessionStatus.Active;

    public void Approve(string by, DateTimeOffset at)
    {
        if (DecidedAt is not null) throw new DomainException("This request has already been answered.");
        (Approved, DecidedAt, DecidedBy, ExpiresAt) = (true, at, by, at + Duration);
    }

    public void Decline(string by, DateTimeOffset at)
    {
        if (DecidedAt is not null) throw new DomainException("This request has already been answered.");
        (Approved, DecidedAt, DecidedBy) = (false, at, by);
    }

    /// <summary>Either side can end it early; ending an ended or expired session changes nothing.</summary>
    public void End(string by, DateTimeOffset at)
    {
        if (!IsActive(at)) return;
        (EndedAt, EndedBy) = (at, by);
    }
}
