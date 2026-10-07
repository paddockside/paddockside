using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Api.Security;

/// <summary>
/// The once-a-minute session check (Identity's security-stamp validator), aware of which tenant a staff member chose.
/// The rebuilt claims know only the person's first staff membership; someone who switched tenant
/// (identity-access.md §3) stays in the one they chose while that membership is active, with its current role. If it
/// was suspended or removed, they fall back to the first one.
/// </summary>
public sealed class SessionStampValidator(
    IOptions<SecurityStampValidatorOptions> options,
    SignInManager<Person> signInManager,
    ILoggerFactory logger,
    PaddocksideIdentityDbContext identity)
    : SecurityStampValidator<Person>(options, signInManager, logger)
{
    protected override async Task SecurityStampVerified(Person user, CookieValidatePrincipalContext context)
    {
        var chosen = context.Principal?.FindFirstValue(SessionClaims.Tenant);
        var isStaffSession = context.Principal?.FindFirstValue(SessionClaims.AudienceClass) == nameof(AudienceClass.Staff)
                             && context.Principal?.HasClaim(c => c.Type == SessionClaims.SupportSession) != true;

        await base.SecurityStampVerified(user, context);

        if (!isStaffSession || !Guid.TryParse(chosen, out var tenantId) || context.Principal?.Identity is not ClaimsIdentity fresh) return;
        if (fresh.FindFirst(SessionClaims.Tenant)?.Value == chosen) return;

        var membership = await identity.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.PersonId == user.Id && m.TenantId == tenantId
            && m.Status == MembershipStatus.Active
            && (m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin));
        if (membership is null) return; // no longer a member there: the first active membership stands

        StaffTenant.Apply(fresh, membership);
    }
}

/// <summary>Puts a staff membership's tenant, role and class on a session's claims.</summary>
public static class StaffTenant
{
    public static void Apply(ClaimsIdentity identity, Membership membership)
    {
        foreach (var type in new[] { SessionClaims.Tenant, SessionClaims.Role, SessionClaims.AudienceClass })
            foreach (var stale in identity.FindAll(type).ToList()) identity.RemoveClaim(stale);
        identity.AddClaim(new Claim(SessionClaims.Tenant, membership.TenantId.ToString()));
        identity.AddClaim(new Claim(SessionClaims.Role, membership.Role.ToString()));
        identity.AddClaim(new Claim(SessionClaims.AudienceClass, membership.Class.ToString()));
    }
}
