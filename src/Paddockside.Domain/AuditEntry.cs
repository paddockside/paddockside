namespace Paddockside.Domain;

/// <summary>Who did something: shown in the log, and decides what operators may read of it.</summary>
public enum AuditActorKind
{
    System,
    Staff,
    Owner,

    /// <summary>A Paddockside operator inside the tenant through an approved support session.</summary>
    Operator,

    /// <summary>Not signed in (a failed sign-in, an invitation being accepted).</summary>
    Anonymous,
}

/// <summary>
/// One line in a tenant's audit log (identity-access.md §8): append-only — never edited, never deleted. Every
/// authentication event and every permission-bearing action: who, what, on which entity, from which IP and device,
/// with the old and new values where something changed.
/// </summary>
public sealed class AuditEntry
{
    /// <summary>For EF Core materialisation.</summary>
    private AuditEntry() => (Action, Summary, ActorName) = (null!, null!, null!);

    public AuditEntry(Guid tenantId, DateTimeOffset at, AuditActorKind actorKind, Guid? actorPersonId, string actorName, string action, string summary,
        string? entityType = null, Guid? entityId = null, string? oldValue = null, string? newValue = null, string? ipAddress = null, string? device = null)
    {
        TenantId = tenantId;
        At = at;
        ActorKind = actorKind;
        ActorPersonId = actorPersonId;
        ActorName = actorName;
        Action = action;
        Summary = summary;
        EntityType = entityType;
        EntityId = entityId;
        OldValue = oldValue;
        NewValue = newValue;
        IpAddress = ipAddress;
        Device = device;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public DateTimeOffset At { get; }

    public AuditActorKind ActorKind { get; }

    public Guid? ActorPersonId { get; }

    /// <summary>As it read at the time (an email address, "Paddockside support: …", "System").</summary>
    public string ActorName { get; }

    /// <summary>A stable code: "auth.signed-in", "member.role-changed", "support.viewed" and so on.</summary>
    public string Action { get; }

    /// <summary>The same in words, for people reading the log.</summary>
    public string Summary { get; }

    public string? EntityType { get; }

    public Guid? EntityId { get; }

    public string? OldValue { get; }

    public string? NewValue { get; }

    public string? IpAddress { get; }

    /// <summary>The browser or app, from its user agent.</summary>
    public string? Device { get; }
}
