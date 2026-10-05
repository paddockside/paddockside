using Microsoft.EntityFrameworkCore;
using Paddockside.Application.Tenancy;

namespace Paddockside.Infrastructure.Persistence;

/// <summary>A tenant context fixed to one tenant, for work that runs outside a signed-in request.</summary>
public sealed class FixedTenantContext(Guid? tenantId) : ITenantContext
{
    public Guid? TenantId => tenantId;
}

/// <summary>
/// Opens a database context acting for one named tenant, behind the same tenant filter as a request. For system
/// work (the email sender, webhooks): everything read or written through it belongs to that tenant only.
/// </summary>
public sealed class TenantScopedDb(DbContextOptions<PaddocksideDbContext> options)
{
    public PaddocksideDbContext For(Guid tenantId) => new(options, new FixedTenantContext(tenantId));

    /// <summary>
    /// The only cross-tenant read: which tenants have queued email. Returns ids and nothing else; all real work
    /// then happens through <see cref="For"/>.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> TenantsWithQueuedEmailAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        return await db.Deliveries.IgnoreQueryFilters()
            .Where(d => d.Status == Domain.DeliveryStatus.Queued && d.Channel == Domain.DeliveryChannel.Email && d.Attempts < Domain.Delivery.MaxAttempts)
            .Select(d => d.TenantId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
