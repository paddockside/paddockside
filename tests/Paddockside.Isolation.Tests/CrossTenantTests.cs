using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests;

/// <summary>
/// The cross-tenant suite (non-functional.md §2, P28). A context acting for tenant A asks for tenant B's
/// data by every read path the data layer offers and must get nothing back. Gates every release.
/// The per-entity tests enumerate the EF model, so a new aggregate is covered the moment it is mapped.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class CrossTenantTests(IsolationDatabase db)
{
    public static TheoryData<string> EntityTypes()
    {
        using var context = new IsolationDatabase().ContextFor(null);
        var data = new TheoryData<string>();
        foreach (var type in RootEntityTypes(context)) data.Add(type.ClrType.Name);
        return data;
    }

    [Fact]
    public void Every_entity_type_has_a_tenant_query_filter()
    {
        using var context = db.ContextFor(db.A.TenantId);

        Assert.All(RootEntityTypes(context), type => Assert.NotNull(type.GetQueryFilter()));
    }

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public async Task Listing_returns_only_the_current_tenants_rows(string entityType)
    {
        await using var a = db.ContextFor(db.A.TenantId);
        await using var b = db.ContextFor(db.B.TenantId);

        var seenByA = await TenantIdsOfAllRows(a, entityType);
        var seenByB = await TenantIdsOfAllRows(b, entityType);

        Assert.NotEmpty(seenByA);
        Assert.All(seenByA, tenantId => Assert.Equal(db.A.TenantId, tenantId));
        Assert.NotEmpty(seenByB);
        Assert.All(seenByB, tenantId => Assert.Equal(db.B.TenantId, tenantId));
    }

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public async Task Find_by_another_tenants_id_returns_nothing(string entityType)
    {
        await using var b = db.ContextFor(db.B.TenantId);
        var keysOfB = await KeysOfAllRows(b, entityType);
        Assert.NotEmpty(keysOfB);

        await using var a = db.ContextFor(db.A.TenantId);
        var clrType = EntityType(a, entityType).ClrType;
        foreach (var key in keysOfB)
            Assert.Null(await a.FindAsync(clrType, key));
    }

    [Theory]
    [MemberData(nameof(EntityTypes))]
    public async Task No_tenant_sees_nothing(string entityType)
    {
        await using var none = db.ContextFor(null);

        Assert.Empty(await TenantIdsOfAllRows(none, entityType));
    }

    [Fact]
    public async Task Lookups_by_another_tenants_ids_return_nothing()
    {
        await using var a = db.ContextFor(db.A.TenantId);

        Assert.Null(await a.Tenants.SingleOrDefaultAsync(t => t.Id == db.B.TenantId));
        Assert.Null(await a.Horses.SingleOrDefaultAsync(h => h.Id == db.B.HorseId));
        Assert.Null(await a.Parties.SingleOrDefaultAsync(p => p.Id == db.B.PartyId));
        Assert.Null(await a.Events.SingleOrDefaultAsync(e => e.Id == db.B.EventId));
        Assert.Null(await a.StreamItems.SingleOrDefaultAsync(s => s.Id == db.B.StreamItemId));
        Assert.False(await a.Horses.AnyAsync(h => h.Id == db.B.HorseId));
    }

    [Fact]
    public async Task Child_queries_by_another_tenants_parent_return_nothing()
    {
        await using var a = db.ContextFor(db.A.TenantId);

        Assert.Empty(await a.ManagementPeriods.Where(p => p.HorseId == db.B.HorseId).ToListAsync());
        Assert.Empty(await a.ManagedInterests.Where(i => i.HorseId == db.B.HorseId || i.PartyId == db.B.PartyId).ToListAsync());
        Assert.Empty(await a.Events.Where(e => e.HorseId == db.B.HorseId).ToListAsync());
        Assert.Empty(await a.StreamItems.Where(s => s.HorseId == db.B.HorseId || s.EventId == db.B.EventId).ToListAsync());
    }

    [Fact]
    public async Task Includes_and_owned_names_stay_inside_the_tenant()
    {
        await using var a = db.ContextFor(db.A.TenantId);

        var horses = await a.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).ToListAsync();

        var horse = Assert.Single(horses);
        Assert.Equal(db.A.HorseId, horse.Id);
        Assert.All(horse.ManagementPeriods, p => Assert.Equal(db.A.TenantId, p.TenantId));
        Assert.All(horse.Interests, i => Assert.Equal(db.A.TenantId, i.TenantId));
        Assert.All(horse.Names, n => Assert.StartsWith("Tenant A", n.Name));
        Assert.Empty(await a.Horses.Where(h => h.Names.Any(n => n.Name.StartsWith("Tenant B"))).ToListAsync());
    }

    [Fact]
    public async Task Joins_and_aggregates_count_only_the_current_tenant()
    {
        await using var a = db.ContextFor(db.A.TenantId);
        await using var b = db.ContextFor(db.B.TenantId);

        var joined = await (
            from item in a.StreamItems
            join horse in a.Horses on item.HorseId equals horse.Id
            select new { item.TenantId, HorseTenant = horse.TenantId }).ToListAsync();

        Assert.NotEmpty(joined);
        Assert.All(joined, row => Assert.Equal((db.A.TenantId, db.A.TenantId), (row.TenantId, row.HorseTenant)));
        Assert.Equal(await b.StreamItems.CountAsync(), await a.StreamItems.CountAsync());
        Assert.Equal(1000L, await a.ManagedInterests.Where(i => i.State == InterestState.Active).SumAsync(i => i.Units));
    }

    [Fact]
    public async Task Raw_sql_is_still_filtered()
    {
        await using var a = db.ContextFor(db.A.TenantId);

        var horses = await a.Horses.FromSqlRaw("SELECT * FROM Horses").ToListAsync();

        Assert.Equal(db.A.HorseId, Assert.Single(horses).Id);
    }

    [Fact]
    public async Task Saving_another_tenants_row_is_refused()
    {
        await using var a = db.ContextFor(db.A.TenantId);
        a.Add(new Party(db.B.TenantId, "Smuggled"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveChangesAsync());
        Assert.Contains("another tenant", ex.Message);
    }

    [Fact]
    public async Task Saving_without_a_tenant_is_refused()
    {
        await using var none = db.ContextFor(null);
        none.Add(new Tenant("Orphan"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => none.SaveChangesAsync());
    }

    private static IEnumerable<IEntityType> RootEntityTypes(DbContext context) =>
        context.Model.GetEntityTypes().Where(t => !t.IsOwned()).OrderBy(t => t.ClrType.Name);

    private static IEntityType EntityType(DbContext context, string name) =>
        RootEntityTypes(context).Single(t => t.ClrType.Name == name);

    private static async Task<List<object>> AllRows(DbContext context, string entityType)
    {
        var clrType = EntityType(context, entityType).ClrType;
        var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(clrType).Invoke(context, null)!;
        return await set.AsNoTracking().ToListAsync();
    }

    private static async Task<List<Guid>> TenantIdsOfAllRows(DbContext context, string entityType) =>
        (await AllRows(context, entityType))
            .Select(row => row is Tenant tenant ? tenant.Id : (Guid)row.GetType().GetProperty("TenantId")!.GetValue(row)!)
            .ToList();

    private static async Task<List<Guid>> KeysOfAllRows(DbContext context, string entityType) =>
        (await AllRows(context, entityType)).Select(row => (Guid)row.GetType().GetProperty("Id")!.GetValue(row)!).ToList();
}
