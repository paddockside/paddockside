using Paddockside.Application.Tenancy;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Api.Tenancy;

/// <summary>
/// The active tenant comes from the signed-in session — the claim stamped at sign-in from the person's
/// membership — and from nowhere else. Route values, query strings and headers are never consulted
/// (non-functional.md §2).
/// </summary>
public sealed class SessionTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid? TenantId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return null;
            return Guid.TryParse(user.FindFirst(SessionClaims.Tenant)?.Value, out var tenantId) ? tenantId : null;
        }
    }
}
