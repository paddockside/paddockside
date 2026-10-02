namespace Paddockside.Domain;

/// <summary>
/// The fixed roles (identity-access.md §5). Tenants may rename them for display but not add or edit them.
/// </summary>
public enum MemberRole
{
    // Staff class
    Viewer,
    Coordinator,
    Manager,
    TenantAdmin,

    // Client class
    Owner,
    OwnerDelegate,
}

public static class MemberRoles
{
    public static AudienceClass ClassOf(MemberRole role) => role switch
    {
        MemberRole.Viewer or MemberRole.Coordinator or MemberRole.Manager or MemberRole.TenantAdmin => AudienceClass.Staff,
        MemberRole.Owner or MemberRole.OwnerDelegate => AudienceClass.Client,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
