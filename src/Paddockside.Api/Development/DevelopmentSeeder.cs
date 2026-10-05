using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paddockside.Application.Tenancy;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Development;

/// <summary>
/// Development only: applies migrations to the local database and, on first run, creates a demo tenant with
/// a few fictional horses and one staff login (from the DevSeed section of appsettings.Development.json).
/// The staff member enrols an authenticator on first sign-in, like anyone else.
/// </summary>
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var identity = provider.GetRequiredService<PaddocksideIdentityDbContext>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DevelopmentSeeder));

        await provider.GetRequiredService<PaddocksideDbContext>().Database.MigrateAsync();
        await identity.Database.MigrateAsync();

        var email = configuration["DevSeed:StaffEmail"];
        var password = configuration["DevSeed:StaffPassword"];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;

        var users = provider.GetRequiredService<UserManager<Person>>();
        var options = provider.GetRequiredService<DbContextOptions<PaddocksideDbContext>>();
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            // Databases seeded before events existed get the demo stream on their next start.
            if (await identity.ActiveStaffMembershipAsync(existing.Id) is { } membership)
                await DemoStream.EnsureAsync(options, membership.TenantId, provider.GetRequiredService<TimeProvider>(), logger);
            return;
        }

        var tenant = new Tenant("Laurel Oak Bloodstock (demo)");
        await using (var db = new PaddocksideDbContext(provider.GetRequiredService<DbContextOptions<PaddocksideDbContext>>(), new FixedTenant(tenant.Id)))
        {
            db.Add(tenant);
            db.AddRange(DemoStable(tenant));
            await db.SaveChangesAsync();
        }

        var person = new Person { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(person, password);
        if (!created.Succeeded)
        {
            logger.LogError("Could not create the demo staff login: {Errors}", string.Join("; ", created.Errors.Select(e => e.Description)));
            return;
        }

        identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = tenant.Id, Role = MemberRole.TenantAdmin });
        await identity.SaveChangesAsync();
        logger.LogInformation("Seeded demo tenant '{Tenant}' with staff login {Email}.", tenant.Name, email);
        await DemoStream.EnsureAsync(options, tenant.Id, provider.GetRequiredService<TimeProvider>(), logger);
    }

    private static IEnumerable<object> DemoStable(Tenant tenant)
    {
        var bought = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.FromHours(10));
        var syndicate = new HoldingEntity(tenant.Id, "Laurel Oak Bloodstock", HoldingEntityType.ManagedSyndicate);
        var house = new Party(tenant.Id, "Laurel Oak house share", PartyKind.Organisation);
        var owners = new[] { "Margaret Hale", "Tom Okafor", "Priya Nair", "Graham Lowe" }.Select(n => new Party(tenant.Id, n)).ToArray();

        var mare = new Horse(tenant.Id, "Faultless Miss", HorseNameKind.Registered, bought);
        mare.OpenManagementPeriod(bought);
        mare.AddInterest(house, syndicate, 100, bought);
        foreach (var owner in owners) mare.AddInterest(owner, syndicate, 225, bought);

        var yearling = new Horse(tenant.Id, "Lot 231 (unnamed)", HorseNameKind.SaleLot, bought.AddYears(1));
        yearling.OpenManagementPeriod(bought.AddYears(1));
        yearling.AddInterest(house, syndicate, 1000, bought.AddYears(1));

        var colt = new Horse(tenant.Id, "Northern Lark", HorseNameKind.Registered, bought);
        colt.OpenManagementPeriod(bought);
        colt.AddInterest(owners[0], syndicate, 500, bought);
        colt.AddInterest(owners[1], syndicate, 500, bought);

        var sold = new Horse(tenant.Id, "Ocean Ridge", HorseNameKind.Registered, bought);
        sold.OpenManagementPeriod(bought);
        sold.AddInterest(owners[2], syndicate, 1000, bought);
        sold.CloseManagementPeriod(bought.AddYears(2), "Sold to a third party");

        return [syndicate, house, .. owners, mare, yearling, colt, sold];
    }

    internal sealed class FixedTenant(Guid tenantId) : ITenantContext
    {
        public Guid? TenantId => tenantId;
    }
}
