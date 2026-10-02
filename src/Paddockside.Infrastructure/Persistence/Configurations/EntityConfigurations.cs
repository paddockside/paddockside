using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Persistence.Configurations;

// The domain uses get-only properties and private collections, so every mapped member is named here and EF
// writes through backing fields. Computed members are ignored explicitly. Enums are stored as strings, and
// every foreign key is Restrict: business records are never cascade-deleted.

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.PriorManagementPeriodsVisible);
    }
}

internal sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.Kind).AsString();
    }
}

internal sealed class HoldingEntityConfiguration : IEntityTypeConfiguration<HoldingEntity>
{
    public void Configure(EntityTypeBuilder<HoldingEntity> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Type).AsString();
        b.Ignore(x => x.ExpandsInternally);
    }
}

internal sealed class HorseConfiguration : IEntityTypeConfiguration<Horse>
{
    public void Configure(EntityTypeBuilder<Horse> b)
    {
        b.MapTenantOwned();
        b.Ignore(x => x.Name);
        b.Ignore(x => x.CurrentManagementPeriod);

        b.OwnsMany(x => x.Names, n =>
        {
            n.ToTable("HorseNames");
            n.WithOwner().HasForeignKey("HorseId");
            n.Property<int>("Id");
            n.HasKey("Id");
            n.Property(x => x.Name).HasMaxLength(200);
            n.Property(x => x.Kind).AsString();
            n.Property(x => x.ValidFrom);
            n.Property(x => x.ValidTo);
            n.Property(x => x.Source).HasMaxLength(200);
            n.Ignore(x => x.IsCurrent);
            n.HasIndex(x => x.Name);
        });
        b.Navigation(x => x.Names).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.ManagementPeriods).WithOne().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.ManagementPeriods).HasField("_periods").UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.Interests).WithOne().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(x => x.Interests).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ManagementPeriodConfiguration : IEntityTypeConfiguration<ManagementPeriod>
{
    public void Configure(EntityTypeBuilder<ManagementPeriod> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.HorseId);
        b.Property(x => x.From);
        b.Property(x => x.To);
        b.Property(x => x.EndReason).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
    }
}

internal sealed class ManagedInterestConfiguration : IEntityTypeConfiguration<ManagedInterest>
{
    public void Configure(EntityTypeBuilder<ManagedInterest> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.HorseId);
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.Units);
        b.Property(x => x.EffectiveFrom);
        b.Property(x => x.EffectiveTo);
        b.Property(x => x.RegisteredFrom);
        b.Property(x => x.RegisteredTo);
        b.Property(x => x.State).AsString();
        b.Property(x => x.AccessPolicy).AsString();
        b.Ignore(x => x.IsActive);

        b.HasOne<ManagementPeriod>().WithMany().HasForeignKey(x => x.ManagementPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.PartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HoldingEntity>().WithMany().HasForeignKey(x => x.HoldingEntityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManagedInterest>().WithMany().HasForeignKey(x => x.DerivedFromId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.EventType).HasMaxLength(100);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Status).AsString();
        b.Property(x => x.KeyDate);
        b.Property(x => x.OpenedAt);
        b.Property(x => x.ClosedAt);

        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.ClosedByPartyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StreamItemConfiguration : IEntityTypeConfiguration<StreamItem>
{
    public void Configure(EntityTypeBuilder<StreamItem> b)
    {
        b.MapTenantOwned();
        b.Property(x => x.Kind).AsString();
        b.Property(x => x.Scope).AsString();
        b.Property(x => x.Direction).AsString();
        b.Property(x => x.OccurredAt);
        b.Property(x => x.RecordedAt);
        b.Property(x => x.Body);
        b.Property(x => x.StepCode).HasMaxLength(100);
        b.Property(x => x.NamedPartyIds)
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<HashSet<Guid>>(json, (JsonSerializerOptions?)null) ?? new HashSet<Guid>(),
                new ValueComparer<IReadOnlySet<Guid>>(
                    (a, c) => a!.SetEquals(c!),
                    ids => ids.Aggregate(0, (hash, id) => hash ^ id.GetHashCode()),
                    ids => ids.ToHashSet()));

        b.HasOne<Horse>().WithMany().HasForeignKey(x => x.HorseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Party>().WithMany().HasForeignKey(x => x.AuthorPartyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.HorseId, x.OccurredAt });
    }
}

internal static class ConfigurationExtensions
{
    /// <summary>Key, tenant column, tenant index and tenant foreign key — the shape every aggregate shares.</summary>
    public static void MapTenantOwned<T>(this EntityTypeBuilder<T> b)
        where T : class
    {
        const string id = "Id", tenantId = PaddocksideDbContext.TenantIdProperty;
        b.HasKey(id);
        b.Property<Guid>(id).ValueGeneratedNever();
        b.Property<Guid>(tenantId);
        b.HasIndex(tenantId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(tenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public static PropertyBuilder<TEnum> AsString<TEnum>(this PropertyBuilder<TEnum> property) =>
        property.HasConversion<string>().HasMaxLength(32);
}
