using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Paddockside.Application.Tenancy;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Persistence;

/// <summary>
/// The database. Every entity type is scoped to a tenant by a global query filter on <c>TenantId</c> (P5),
/// read from <see cref="ITenantContext"/>; writes for any other tenant are refused at save time.
/// </summary>
public sealed class PaddocksideDbContext(DbContextOptions<PaddocksideDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    internal const string TenantIdProperty = "TenantId";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Party> Parties => Set<Party>();

    public DbSet<HoldingEntity> HoldingEntities => Set<HoldingEntity>();

    public DbSet<Horse> Horses => Set<Horse>();

    public DbSet<ManagementPeriod> ManagementPeriods => Set<ManagementPeriod>();

    public DbSet<ManagedInterest> ManagedInterests => Set<ManagedInterest>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<StreamItem> StreamItems => Set<StreamItem>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<ItemRead> ItemReads => Set<ItemRead>();

    public DbSet<RoutingAddress> RoutingAddresses => Set<RoutingAddress>();

    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();

    public DbSet<OwnerInvitation> OwnerInvitations => Set<OwnerInvitation>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<SupportSession> SupportSessions => Set<SupportSession>();

    /// <summary>Read by the query filters. EF Core re-evaluates it for each context instance.</summary>
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Value objects stored inside their owner's row (as JSON), not tables of their own.
        modelBuilder.Ignore<FactField>();
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaddocksideDbContext).Assembly);
        ApplyTenantFilters(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureChangesBelongToCurrentTenant();
        Inbound.HorseInboxes.Issue(this, HorsesGainingNames(), DateTimeOffset.UtcNow);
        ClientAccess.OwnerInvitations.Queue(this, DateTimeOffset.UtcNow);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureChangesBelongToCurrentTenant();
        await Inbound.HorseInboxes.IssueAsync(this, HorsesGainingNames(), DateTimeOffset.UtcNow, cancellationToken);
        await ClientAccess.OwnerInvitations.QueueAsync(this, DateTimeOffset.UtcNow, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>New horses, and horses being given a new name: each needs its inbox address (§3.1).</summary>
    private List<Horse> HorsesGainingNames()
    {
        var renamed = ChangeTracker.Entries<HorseName>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Property("HorseId").CurrentValue)
            .OfType<Guid>()
            .ToHashSet();
        return ChangeTracker.Entries<Horse>()
            .Where(e => e.State == EntityState.Added || renamed.Contains(e.Entity.Id))
            .Select(e => e.Entity)
            .ToList();
    }

    /// <summary>
    /// Filters every root entity type by tenant: <see cref="Tenant"/> by its own id, everything else by
    /// <c>TenantId</c>. A root entity type without one fails model building, so nothing ships unfiltered.
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        var currentTenantId = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned()))
        {
            var keyName = entityType.ClrType == typeof(Tenant) ? nameof(Tenant.Id) : TenantIdProperty;
            if (entityType.FindProperty(keyName) is null)
                throw new InvalidOperationException($"{entityType.ClrType.Name} has no {TenantIdProperty}; every aggregate must carry one.");

            var entity = Expression.Parameter(entityType.ClrType, "e");
            var tenantId = Expression.Convert(Expression.Property(entity, keyName), typeof(Guid?));
            entityType.SetQueryFilter(Expression.Lambda(Expression.Equal(tenantId, currentTenantId), entity));
        }
    }

    private void EnsureChangesBelongToCurrentTenant()
    {
        var current = CurrentTenantId ?? throw new InvalidOperationException("Cannot save changes without an active tenant.");

        // The audit log is append-only (identity-access.md §8): entries are written once and never changed or removed.
        if (ChangeTracker.Entries<AuditEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit entries cannot be changed or deleted.");

        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Metadata.IsOwned()) continue;
            if (OwningTenantOf(entry) != current)
                throw new InvalidOperationException($"Refusing to save a {entry.Metadata.ClrType.Name} that belongs to another tenant.");
        }
    }

    private static Guid OwningTenantOf(EntityEntry entry) => entry.Entity is Tenant tenant
        ? tenant.Id
        : (Guid)entry.Property(TenantIdProperty).CurrentValue!;
}
