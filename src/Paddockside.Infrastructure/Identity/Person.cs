using Microsoft.AspNetCore.Identity;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Identity;

/// <summary>
/// One person, one identity across the whole product (identity-access.md §3). Product-level, so not
/// tenant-scoped: tenants reach a person only through a <see cref="Membership"/>.
/// </summary>
public sealed class Person : IdentityUser<Guid>
{
    public Person()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public List<Membership> Memberships { get; } = [];

    /// <summary>
    /// One of us, the product operator (identity-access.md §7): signs in to the operator console, which shows tenants'
    /// metadata only, never their horses, parties or messages. Granted from the command line, never from the app.
    /// </summary>
    public bool IsOperator { get; set; }
}

public enum MembershipStatus
{
    Active,
    Suspended,
}

/// <summary>A person's role in one tenant (identity-access.md §3, §6).</summary>
public sealed class Membership
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PersonId { get; init; }

    public Guid TenantId { get; init; }

    public MemberRole Role { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who invited them, for staff memberships created from an invitation.</summary>
    public Guid? InvitedByPersonId { get; set; }

    /// <summary>When the person first signed in under this membership (accepting an invitation or any link).</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    public AudienceClass Class => MemberRoles.ClassOf(Role);
}
