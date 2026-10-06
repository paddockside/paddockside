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

    /// <summary>The staff roles, most capable first.</summary>
    public static readonly IReadOnlyList<MemberRole> StaffRoles = [MemberRole.TenantAdmin, MemberRole.Manager, MemberRole.Coordinator, MemberRole.Viewer];

    /// <summary>The role's name as people say it.</summary>
    public static string Label(MemberRole role) => role switch
    {
        MemberRole.TenantAdmin => "Tenant admin",
        MemberRole.OwnerDelegate => "Owner delegate",
        _ => role.ToString(),
    };

    /// <summary>One plain line on what the role can do (identity-access.md §5), shown wherever a role is chosen.</summary>
    public static string Describe(MemberRole role) => role switch
    {
        MemberRole.Viewer => "Reads everything, including staff notes, but cannot post or change anything.",
        MemberRole.Coordinator => "The everyday role: posts updates, messages owners and trainers, works the inbound queue.",
        MemberRole.Manager => "Everything a coordinator does, plus ownership changes, imports and exports.",
        MemberRole.TenantAdmin => "Everything a manager does, plus members, roles, settings and billing.",
        MemberRole.Owner => "Sees their own horses and the updates shared with owners.",
        MemberRole.OwnerDelegate => "Acts for one owner, such as a partner or accountant.",
        _ => "",
    };
}
