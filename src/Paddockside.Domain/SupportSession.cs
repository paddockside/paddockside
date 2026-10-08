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
/// <para>
/// Emergency access, for a tenant with no reachable admin: the request needs a second, different operator instead
/// of a tenant admin, and the tenant is told by email when it starts.
/// </para>
/// </summary>
public sealed class SupportSession
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    /// <summary>For EF Core materialisation.</summary>
    private SupportSession() => (OperatorName, Reason) = (null!, null!);

    public SupportSession(Guid tenantId, Guid operatorPersonId, string operatorName, string reason, TimeSpan duration, DateTimeOffset at, bool emergency = false)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why you need access; the tenant admin reads it before approving.");
        if (duration <= TimeSpan.Zero || duration > MaxDuration) throw new DomainException("A support session lasts at most 24 hours.");
        TenantId = tenantId;
        OperatorPersonId = operatorPersonId;
        OperatorName = operatorName;
        Reason = reason.Trim();
        Duration = duration;
        RequestedAt = at;
        Emergency = emergency;
    }

    /// <summary>No tenant admin could be reached: a second operator approves instead (identity-access.md §7).</summary>
    public bool Emergency { get; }

    /// <summary>For emergency access: the second operator who approved it.</summary>
    public Guid? SecondOperatorPersonId { get; private set; }

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

    /// <summary>Approved by one of the tenant's admins, who can never be the operator who asked.</summary>
    public void Approve(Guid approverPersonId, string by, DateTimeOffset at)
    {
        if (approverPersonId == OperatorPersonId)
            throw new DomainException("You asked for this access, so another admin of this business must approve it.");
        Open(by, at);
    }

    private void Open(string by, DateTimeOffset at)
    {
        if (DecidedAt is not null) throw new DomainException("This request has already been answered.");
        (Approved, DecidedAt, DecidedBy, ExpiresAt) = (true, at, by, at + Duration);
    }

    /// <summary>Emergency access approved by a second operator — never the one who asked.</summary>
    public void ApproveAsSecondOperator(Guid operatorPersonId, string by, DateTimeOffset at)
    {
        if (!Emergency) throw new DomainException("Only emergency access is approved by a second operator; ask the tenant's admins.");
        if (operatorPersonId == OperatorPersonId) throw new DomainException("A second, different operator must approve emergency access.");
        Open(by, at);
        SecondOperatorPersonId = operatorPersonId;
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
