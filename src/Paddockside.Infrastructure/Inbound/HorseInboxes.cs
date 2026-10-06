using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>
/// Each horse's own inbox addresses (messaging-channels.md §3.1): one per primary name the horse has ever had — the
/// sale lot while unnamed, then each registered name — so an old address keeps working after a rename and a lot
/// address survives naming. Stable names are nicknames and get no address. Issuing is just a row; the inbound
/// subdomain is a catch-all. A slug another horse already has gets a number (<c>bel-esprit-2</c>), so every horse
/// always has an address.
/// <para>
/// Addresses are issued automatically: <see cref="PaddocksideDbContext"/> calls <see cref="IssueAsync"/> whenever a
/// horse or a new name is saved, and <see cref="EnsureAsync"/> backfills horses saved before that.
/// </para>
/// </summary>
public static class HorseInboxes
{
    /// <summary>Adds any missing addresses for the context's tenant and saves them. Returns how many were added.</summary>
    public static async Task<int> EnsureAsync(PaddocksideDbContext db, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var horses = await db.Horses.AsNoTracking().ToListAsync(cancellationToken);
        var added = await IssueAsync(db, horses, at, cancellationToken);
        if (added > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>Adds the missing addresses for these horses to the context, unsaved. Returns how many.</summary>
    public static async Task<int> IssueAsync(PaddocksideDbContext db, IReadOnlyCollection<Horse> horses, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (horses.Count == 0) return 0;
        var stored = await db.RoutingAddresses
            .Where(r => r.Kind == RoutingAddressKind.HorseInbox)
            .Select(r => new { r.Token, r.HorseId })
            .ToListAsync(cancellationToken);
        return Issue(db, horses, stored.Select(r => (r.Token, r.HorseId)), at);
    }

    /// <summary>The same, for the synchronous SaveChanges.</summary>
    public static int Issue(PaddocksideDbContext db, IReadOnlyCollection<Horse> horses, DateTimeOffset at)
    {
        if (horses.Count == 0) return 0;
        var stored = db.RoutingAddresses
            .Where(r => r.Kind == RoutingAddressKind.HorseInbox)
            .Select(r => new { r.Token, r.HorseId })
            .ToList();
        return Issue(db, horses, stored.Select(r => (r.Token, r.HorseId)), at);
    }

    /// <summary>
    /// The address to show for a horse: the one for its current name, else the newest. Null when it has none yet.
    /// </summary>
    public static RoutingAddress? Current(Horse horse, IEnumerable<RoutingAddress> addresses)
    {
        var own = addresses.Where(a => a.Kind == RoutingAddressKind.HorseInbox && a.HorseId == horse.Id).ToList();
        var slug = HorseSlug.From(horse.Name);
        return own.FirstOrDefault(a => slug is not null && (a.Token == slug || Numbered(slug).IsMatch(a.Token)))
               ?? own.OrderByDescending(a => a.CreatedAt).FirstOrDefault();
    }

    private static int Issue(PaddocksideDbContext db, IReadOnlyCollection<Horse> horses, IEnumerable<(string Token, Guid HorseId)> stored, DateTimeOffset at)
    {
        // Taken slugs: stored ones plus any added in this unit of work (two new horses with the same name).
        var taken = stored.ToDictionary(r => r.Token, r => r.HorseId);
        foreach (var pending in db.ChangeTracker.Entries<RoutingAddress>().Where(e => e.State == EntityState.Added && e.Entity.Kind == RoutingAddressKind.HorseInbox))
            taken.TryAdd(pending.Entity.Token, pending.Entity.HorseId);

        var added = 0;
        foreach (var horse in horses)
        {
            foreach (var name in horse.Names.Where(n => n.IsPrimary).OrderBy(n => n.ValidFrom))
            {
                if (HorseSlug.From(name.Name) is not { } slug) continue;
                if (SlugFor(slug, horse.Id, taken) is not { } fresh) continue; // this horse already has it

                db.RoutingAddresses.Add(RoutingAddress.ForHorse(horse, fresh, at));
                taken[fresh] = horse.Id;
                added++;
            }
        }

        return added;
    }

    /// <summary>The slug to issue, numbered if another horse has it; null when this horse already holds one.</summary>
    private static string? SlugFor(string slug, Guid horseId, Dictionary<string, Guid> taken)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? slug : $"{slug[..Math.Min(slug.Length, HorseSlug.MaxLength - 4)].TrimEnd('-')}-{n}";
            if (!taken.TryGetValue(candidate, out var holder)) return candidate;
            if (holder == horseId) return null;
        }
    }

    private static Regex Numbered(string slug) => new($"^{Regex.Escape(slug)}-[0-9]+$");
}
