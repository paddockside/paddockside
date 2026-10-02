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

    /// <summary>Read by the query filters. EF Core re-evaluates it for each context instance.</summary>
    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaddocksideDbContext).Assembly);
        ApplyTenantFilters(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureChangesBelongToCurrentTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureChangesBelongToCurrentTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
