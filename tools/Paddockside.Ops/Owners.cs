using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

/// <summary>
/// Setting up test owners until staff can do it in the app (build plan Sprint 4, "Ownership &amp; import"). Goes
/// through the domain model, so the same rules apply as anywhere else, and saving queues the owner's invitation
/// as the tenant's setting says.
/// </summary>
internal static class Owners
{
    public static async Task<int> ListHorsesAsync(string connection, string slug)
    {
        if (await TenantAsync(connection, slug) is not { } tenant) return 1;
        await using var db = For(connection, tenant.Id);
        var horses = await LoadHorsesAsync(db);
        var parties = await db.Parties.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.PrimaryEmail is { } e ? $"{p.DisplayName} <{e.Value}>" : p.DisplayName);

        Console.WriteLine($"{tenant.Name}:");
        foreach (var horse in horses.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            var period = horse.CurrentManagementPeriod;
            var owners = period is null ? [] : horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive)
                .Select(i => $"{parties.GetValueOrDefault(i.PartyId) ?? "?"} {i.Units / 10m:0.#}%").ToList();
            Console.WriteLine($"  {horse.Name}{(period is null ? "  (not managed: owners cannot be added)" : "")}");
            foreach (var owner in owners) Console.WriteLine($"      {owner}");
        }

        return 0;
    }

    public static async Task<int> AddOwnerAsync(string connection, string slug, string horseName, string address, string name, string percentText)
    {
        if (!decimal.TryParse(percentText.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) || percent <= 0 || percent > 100)
        {
            Console.Error.WriteLine("The share is a percentage between 0 and 100, e.g. 5 or 2.5.");
            return 2;
        }

        if (await TenantAsync(connection, slug) is not { } tenant) return 1;
        await using var db = For(connection, tenant.Id);
        var horse = (await LoadHorsesAsync(db, tracking: true)).SingleOrDefault(h => string.Equals(h.Name, horseName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (horse is null)
        {
            Console.Error.WriteLine($"No horse called \"{horseName}\" in {tenant.Name}. Run list-horses {slug} to see their names.");
            return 1;
        }

        if (horse.CurrentManagementPeriod is not { } period)
        {
            Console.Error.WriteLine($"{horse.Name} is not managed at the moment, so it has no owners to add to.");
            return 1;
        }

        var normalised = address.Trim().ToLowerInvariant();
        var party = (await db.Parties.ToListAsync()).FirstOrDefault(p => p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == normalised));
        if (party is null)
        {
            party = new Party(tenant.Id, name.Trim());
            party.AddEmail(normalised);
            db.Parties.Add(party);
        }
        else if (horse.Interests.Any(i => i.PartyId == party.Id && i.ManagementPeriodId == period.Id && i.IsActive))
        {
            Console.Error.WriteLine($"{party.DisplayName} ({normalised}) already owns part of {horse.Name}.");
            return 1;
        }

        // Behind the same holding as the horse's other owners; a horse with none gets its own syndicate.
        var holdingId = horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.HoldingEntityId).FirstOrDefault();
        var holding = holdingId == Guid.Empty ? null : await db.Set<HoldingEntity>().SingleOrDefaultAsync(h => h.Id == holdingId);
        if (holding is null || !holding.ExpandsInternally)
        {
            holding = new HoldingEntity(tenant.Id, $"{horse.Name} Syndicate", HoldingEntityType.ManagedSyndicate);
            db.Add(holding);
        }

        try
        {
            horse.AddInterest(party, holding, (long)Math.Round(percent * 10), DateTimeOffset.UtcNow);
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        await db.SaveChangesAsync();
        Console.WriteLine($"{party.DisplayName} ({normalised}) now owns {percent:0.#}% of {horse.Name} with {tenant.Name}.");
        Console.WriteLine(tenant.InviteOwnersOnFirstInterest
            ? "Their invitation to the owners' portal is queued, or they can sign in now with \"Email me a link\"."
            : "They can sign in now with \"Email me a link\" on the owners' sign-in page.");
        return 0;
    }

    private static async Task<Tenant?> TenantAsync(string connection, string slug)
    {
        await using var any = For(connection, null);
        var tenant = await any.Tenants.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug.Trim().ToLowerInvariant());
        if (tenant is null)
        {
            var slugs = await any.Tenants.IgnoreQueryFilters().AsNoTracking().Select(t => t.Slug).ToListAsync();
            Console.Error.WriteLine($"No business \"{slug}\". These exist: {string.Join(", ", slugs)}");
        }

        return tenant;
    }

    private static Task<List<Horse>> LoadHorsesAsync(PaddocksideDbContext db, bool tracking = false)
    {
        var query = db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery();
        return (tracking ? query : query.AsNoTracking()).ToListAsync();
    }

    private static PaddocksideDbContext For(string connection, Guid? tenantId) =>
        new(new DbContextOptionsBuilder<PaddocksideDbContext>().UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options, new FixedTenantContext(tenantId));
}
