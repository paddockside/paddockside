using System.Security.Claims;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Audit;

/// <summary>
/// Writes a tenant's audit log (identity-access.md §8). The actor, IP address and device come from the current
/// request, so callers say only what happened. Written through its own tenant-scoped context, so an entry is
/// recorded even when the action itself was refused or happened before anyone was signed in.
/// </summary>
public sealed class AuditLog(TenantScopedDb tenants, IHttpContextAccessor accessor, TimeProvider clock)
{
    public async Task RecordAsync(Guid tenantId, string action, string summary, string? entityType = null, Guid? entityId = null,
        string? oldValue = null, string? newValue = null, (Guid? Id, string Name)? actor = null, CancellationToken cancellationToken = default)
    {
        var http = accessor.HttpContext;
        var (kind, personId, name) = actor is { } given ? (KindOf(http?.User), given.Id, given.Name) : Actor(http?.User);
        await using var db = tenants.For(tenantId);
        db.AuditEntries.Add(new AuditEntry(tenantId, clock.GetUtcNow(), kind, personId, name, action, summary, entityType, entityId,
            Trim(oldValue, 1000), Trim(newValue, 1000), IpAddress(http), Trim(http?.Request.Headers.UserAgent.ToString(), 300)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (AuditActorKind, Guid?, string) Actor(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return (AuditActorKind.Anonymous, null, "Not signed in");
        var id = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : (Guid?)null;
        var name = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity.Name ?? "Unknown";
        var kind = KindOf(user);
        return (kind, id, kind == AuditActorKind.Operator ? $"Paddockside support: {name}" : name);
    }

    private static AuditActorKind KindOf(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated != true ? AuditActorKind.Anonymous
        : user.HasClaim(c => c.Type == SessionClaims.SupportSession) ? AuditActorKind.Operator
        : user.FindFirstValue(SessionClaims.AudienceClass) == nameof(AudienceClass.Client) ? AuditActorKind.Owner
        : AuditActorKind.Staff;

    /// <summary>App Service puts the visitor's address first in X-Forwarded-For; locally it is the connection's.</summary>
    private static string? IpAddress(HttpContext? http)
    {
        if (http is null) return null;
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        var first = forwarded.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Trim(first ?? http.Connection.RemoteIpAddress?.ToString(), 64);
    }

    private static string? Trim(string? value, int max) => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
