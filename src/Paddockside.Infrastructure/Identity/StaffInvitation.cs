using Paddockside.Domain;

namespace Paddockside.Infrastructure.Identity;

/// <summary>
/// An invitation to join a tenant's staff (identity-access.md §6): it names the tenant and the role, is a link
/// valid 14 days, single use. Accepting it creates the person if new and the membership. Product-level, like the
/// person it creates; only the hash of the link's token is stored.
/// </summary>
public sealed class StaffInvitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; init; }

    /// <summary>Kept so the invitation can be shown before anyone is signed in to that tenant.</summary>
    public string TenantName { get; init; } = "";

    public string Email { get; init; } = "";

    public MemberRole Role { get; init; }

    public Guid InvitedByPersonId { get; init; }

    public string InvitedByName { get; init; } = "";

    public string TokenHash { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedByPersonId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsOpen(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}
