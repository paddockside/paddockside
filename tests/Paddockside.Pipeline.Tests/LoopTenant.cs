using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Application.Tenancy;
using Paddockside.Domain;
using Paddockside.Infrastructure;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Pipeline.Tests;

/// <summary>What the loop test acts on: its own tenant, one horse, an open race start, a staff login.</summary>
public sealed record LoopTenant(Guid TenantId, Guid EventId, string StaffEmail, string StaffPassword, string AuthenticatorKey);

/// <summary>
/// Sets up the loop test's own tenant in the dev database ("Paddockside Loop Test", slug <c>looptest</c>), created
/// the first time and reused after: one owner whose email is your test mailbox, a horse, an open race start in
/// window, and a staff member whose password and authenticator key are replaced on every run, so no credential
/// lives anywhere but this process.
/// </summary>
public static class LoopTenantSetup
{
    public const string Slug = "looptest";
    private const string StaffEmail = "loop-test@paddockside.test";

    public static async Task<LoopTenant> EnsureAsync(LoopTestSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddPaddocksideDatabase(settings.ConnectionString);
        services.AddIdentityCore<Person>()
            .AddEntityFrameworkStores<PaddocksideIdentityDbContext>()
            .AddTokenProvider<AuthenticatorTokenProvider<Person>>(TokenOptions.DefaultAuthenticatorProvider);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var tenants = scope.ServiceProvider.GetRequiredService<TenantScopedDb>();
        var tenantId = await tenants.TenantIdForSlugAsync(Slug, CancellationToken.None) ?? await CreateTenantAsync(tenants);

        await using var db = tenants.For(tenantId);
        await EnsureOwnerAsync(db, settings.Mailbox);
        var eventId = await EnsureOpenRaceAsync(db);

        var (password, key) = await EnsureStaffAsync(scope.ServiceProvider, tenantId);
        return new LoopTenant(tenantId, eventId, StaffEmail, password, key);
    }

    private static async Task<Guid> CreateTenantAsync(TenantScopedDb tenants)
    {
        var tenant = new Tenant("Paddockside Loop Test", Slug);
        tenant.SetBranding(Slug, null, "Automated end-to-end test tenant · not a real business");
        var now = DateTimeOffset.UtcNow;
        var syndicate = new HoldingEntity(tenant.Id, "Loop Test Syndicate", HoldingEntityType.ManagedSyndicate);
        var owner = new Party(tenant.Id, "Loop Owner");
        var horse = new Horse(tenant.Id, "Loop Runner", HorseNameKind.Registered, now.AddYears(-1));
        horse.OpenManagementPeriod(now.AddYears(-1));
        horse.AddInterest(owner, syndicate, 1000, now.AddYears(-1));

        await using var db = tenants.For(tenant.Id);
        db.AddRange(tenant, syndicate, owner, horse);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>The owner's email is whatever mailbox this run uses, and is never left marked as bounced.</summary>
    private static async Task EnsureOwnerAsync(PaddocksideDbContext db, string mailbox)
    {
        var owner = await db.Parties.SingleAsync(p => p.DisplayName == "Loop Owner");
        if (!owner.Contacts.Any(c => c.Kind == ContactKind.Email && c.Matches(mailbox))) owner.AddEmail(mailbox);
        owner.ClearUndeliverable(mailbox);
        await db.SaveChangesAsync();
    }

    /// <summary>An open race start whose window includes today, so a reply lands on it; reused while it lasts.</summary>
    private static async Task<Guid> EnsureOpenRaceAsync(PaddocksideDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var open = (await db.Events.Where(e => e.Status == EventStatus.Open).ToListAsync())
            .Where(e => e.KeyDate > now.AddDays(1) && e.IsInWindow(now))
            .OrderBy(e => e.KeyDate)
            .FirstOrDefault();
        if (open is not null) return open.Id;

        var horse = await db.Horses.SingleAsync();
        var race = new Event(horse, "RaceStart", $"Loop test race, {now.AddDays(14):d MMM yyyy}", now.AddDays(14));
        race.Open(now);
        db.Events.Add(race);
        await db.SaveChangesAsync();
        return race.Id;
    }

    private static async Task<(string Password, string Key)> EnsureStaffAsync(IServiceProvider services, Guid tenantId)
    {
        var users = services.GetRequiredService<UserManager<Person>>();
        var person = await users.FindByEmailAsync(StaffEmail);
        if (person is null)
        {
            person = new Person { UserName = StaffEmail, Email = StaffEmail, EmailConfirmed = true };
            Check(await users.CreateAsync(person));
            var identity = services.GetRequiredService<PaddocksideIdentityDbContext>();
            identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = tenantId, Role = MemberRole.Coordinator });
            await identity.SaveChangesAsync();
        }

        // Fresh credentials every run: a random password and a new authenticator key, known only to this process.
        var password = $"loop-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        if (await users.HasPasswordAsync(person)) Check(await users.RemovePasswordAsync(person));
        Check(await users.AddPasswordAsync(person, password));
        Check(await users.ResetAuthenticatorKeyAsync(person));
        Check(await users.SetTwoFactorEnabledAsync(person, true));
        Check(await users.ResetAccessFailedCountAsync(person));
        Check(await users.SetLockoutEndDateAsync(person, null));
        var key = await users.GetAuthenticatorKeyAsync(person) ?? throw new InvalidOperationException("No authenticator key.");
        return (password, key);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
