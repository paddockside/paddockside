using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Paddockside.Application.Tenancy;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Isolation.Tests;

/// <summary>
/// A throwaway SQL Server database, built by applying the real migrations and seeded with two tenants that
/// have the same shape of data. Uses <c>PADDOCKSIDE_TEST_SQL</c> when set (CI), otherwise LocalDB.
/// Fails — never skips — when no server is reachable: an isolation suite that silently does not run is
/// worse than none.
/// </summary>
public sealed class IsolationDatabase : IAsyncLifetime
{
    private const string LocalDb = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _connectionString;

    public IsolationDatabase()
    {
        var server = Environment.GetEnvironmentVariable("PADDOCKSIDE_TEST_SQL") is { Length: > 0 } configured ? configured : LocalDb;
        _connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = $"PaddocksideIsolation_{Guid.NewGuid():N}" }.ConnectionString;
    }

    public SeededTenant A { get; private set; } = null!;

    public SeededTenant B { get; private set; } = null!;

    public PaddocksideDbContext ContextFor(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<PaddocksideDbContext>().UseSqlServer(_connectionString).Options, new FixedTenant(tenantId));

    public async Task InitializeAsync()
    {
        await using (var context = ContextFor(null))
            await context.Database.MigrateAsync();

        A = await SeedAsync("Tenant A");
        B = await SeedAsync("Tenant B");
    }

    public async Task DisposeAsync()
    {
        await using var context = ContextFor(null);
        await context.Database.EnsureDeletedAsync();
    }

    /// <summary>One of everything, including a transfer (so derived interests exist) and a named-parties item.</summary>
    private async Task<SeededTenant> SeedAsync(string name)
    {
        var tenant = new Tenant(name);
        var bought = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.FromHours(10));

        var ann = new Party(tenant.Id, $"{name} Ann");
        var dee = new Party(tenant.Id, $"{name} Dee");
        var eve = new Party(tenant.Id, $"{name} Eve");
        var syndicate = new HoldingEntity(tenant.Id, $"{name} Syndicate", HoldingEntityType.ManagedSyndicate);
        var external = new HoldingEntity(tenant.Id, $"{name} External", HoldingEntityType.External);

        var horse = new Horse(tenant.Id, $"{name} Mare", HorseNameKind.Registered, bought);
        horse.AddName($"{name} Stable Name", HorseNameKind.StableName, bought);
        horse.OpenManagementPeriod(bought);
        horse.AddInterest(ann, syndicate, 500, bought);
        var deeInterest = horse.AddInterest(dee, syndicate, 500, bought);
        horse.TransferInterest(deeInterest.Id, bought.AddYears(2), [(eve, 500)]);

        var raceStart = new Event(horse, "RaceStart", $"{name} race", bought.AddMonths(6));
        raceStart.Open(bought.AddMonths(5));
        var update = new StreamItem(horse, raceStart, StreamItemKind.Message, StreamItemScope.Owners, StreamItemDirection.Outbound, bought.AddMonths(6), $"{name} update");
        var personal = new StreamItem(horse, null, StreamItemKind.Message, StreamItemScope.NamedParties, StreamItemDirection.Outbound, bought.AddMonths(7), $"{name} personal", namedPartyIds: [ann.Id]);
        var note = new StreamItem(horse, raceStart, StreamItemKind.Note, StreamItemScope.Internal, StreamItemDirection.Internal, bought.AddMonths(6), $"{name} note");

        await using var context = ContextFor(tenant.Id);
        context.AddRange(tenant, ann, dee, eve, syndicate, external, horse, raceStart, update, personal, note);
        await context.SaveChangesAsync();

        return new SeededTenant(tenant.Id, horse.Id, ann.Id, raceStart.Id, update.Id);
    }

    private sealed class FixedTenant(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}

public sealed record SeededTenant(Guid TenantId, Guid HorseId, Guid PartyId, Guid EventId, Guid StreamItemId);

[CollectionDefinition(Name)]
public sealed class IsolationCollection : ICollectionFixture<IsolationDatabase>
{
    public const string Name = "Isolation database";
}
