using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Identity;

/// <summary>Claims carried in the session cookie.</summary>
public static class SessionClaims
{
    /// <summary>The active tenant, stamped at sign-in from the person's membership (never from a request).</summary>
    public const string Tenant = "paddockside:tenant";

    public const string Role = "paddockside:role";

    public const string AudienceClass = "paddockside:class";
}

/// <summary>Finds the membership a staff sign-in acts under.</summary>
public static class StaffMemberships
{
    /// <summary>
    /// The person's active staff membership. With several, the oldest wins until the tenant switcher exists
    /// (identity-access.md §3).
    /// </summary>
    public static Task<Membership?> ActiveStaffMembershipAsync(this PaddocksideIdentityDbContext db, Guid personId) =>
        db.Memberships
            .Where(m => m.PersonId == personId && m.Status == MembershipStatus.Active)
            .Where(m => m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin)
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync();
}

/// <summary>Adds the active tenant, role and audience class to the session at sign-in and on refresh.</summary>
public sealed class StaffClaimsPrincipalFactory(
    UserManager<Person> userManager,
    IOptions<IdentityOptions> options,
    PaddocksideIdentityDbContext db)
    : UserClaimsPrincipalFactory<Person>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Person user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (await db.ActiveStaffMembershipAsync(user.Id) is { } membership)
        {
            identity.AddClaim(new Claim(SessionClaims.Tenant, membership.TenantId.ToString()));
            identity.AddClaim(new Claim(SessionClaims.Role, membership.Role.ToString()));
            identity.AddClaim(new Claim(SessionClaims.AudienceClass, membership.Class.ToString()));
        }

        return identity;
    }
}
