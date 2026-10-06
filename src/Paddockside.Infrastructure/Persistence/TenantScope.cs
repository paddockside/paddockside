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

    // The cross-tenant reads. Each returns ids and nothing else; all real work then happens through For().

    /// <summary>
    /// The parties, in any tenant, with this verified-on-receipt contact (identity-access.md §3): a person proving
    /// they read that inbox or phone is linked to every party holding it.
    /// </summary>
    public async Task<IReadOnlyList<(Guid TenantId, Guid PartyId, Guid? PersonId)>> PartiesWithContactAsync(Domain.ContactKind kind, string value, CancellationToken cancellationToken)
    {
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        var rows = await db.Parties.IgnoreQueryFilters()
            .Where(p => p.Contacts.Any(c => c.Kind == kind && c.Value == value))
            .Select(p => new { p.TenantId, p.Id, p.PersonId })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.TenantId, r.Id, r.PersonId)).ToList();
    }

    /// <summary>Which tenants have owner invitations waiting to go out.</summary>
    public async Task<IReadOnlyList<Guid>> TenantsWithQueuedInvitationsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        return await db.OwnerInvitations.IgnoreQueryFilters()
            .Where(i => i.Status == Domain.OwnerInvitationStatus.Queued)
            .Select(i => i.TenantId)
            .Distinct()
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Every tenant, for start-up housekeeping that then works one tenant at a time.</summary>
    public async Task<IReadOnlyList<Guid>> AllTenantIdsAsync(CancellationToken cancellationToken)
    {
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        return await db.Tenants.IgnoreQueryFilters().Select(t => t.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Which tenant an inbound subdomain (<c>{slug}.in.…</c>) belongs to.</summary>
    public async Task<Guid?> TenantIdForSlugAsync(string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(slug)) return null;
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        var ids = await db.Tenants.IgnoreQueryFilters().Where(t => t.Slug == slug).Select(t => t.Id).ToListAsync(cancellationToken);
        return ids.Count == 1 ? ids[0] : null;
    }

    /// <summary>Inbound messages stored but not yet processed, e.g. queued in memory before a restart.</summary>
    public async Task<IReadOnlyList<(Guid TenantId, Guid Id)>> UnprocessedInboundAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = new PaddocksideDbContext(options, new FixedTenantContext(null));
        var rows = await db.InboundMessages.IgnoreQueryFilters()
            .Where(m => m.State == Domain.InboundState.Received)
            .OrderBy(m => m.ReceivedAt)
            .Select(m => new { m.TenantId, m.Id })
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.TenantId, r.Id)).ToList();
    }

    /// <summary>Which tenants have queued email.</summary>
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
