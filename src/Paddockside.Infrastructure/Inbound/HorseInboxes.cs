using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>
/// Issues each horse its inbox addresses: one per name the horse has ever had, so a renamed horse's old address
/// keeps working (messaging-channels.md §3.1). Issuing is just a row; the inbound subdomain is a catch-all.
/// Two horses whose names make the same slug: the first keeps it, and mail to it lands on that horse.
/// </summary>
public static class HorseInboxes
{
    /// <summary>Adds any missing addresses for the context's tenant and saves them. Returns how many were added.</summary>
    public static async Task<int> EnsureAsync(PaddocksideDbContext db, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var horses = await db.Horses.AsNoTracking().OrderBy(h => h.Id).ToListAsync(cancellationToken);
        var taken = (await db.RoutingAddresses
                .Where(r => r.Kind == RoutingAddressKind.HorseInbox)
                .Select(r => new { r.Token, r.HorseId })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.Token, r => r.HorseId);

        var added = 0;
        foreach (var horse in horses)
        {
            foreach (var name in horse.Names.OrderBy(n => n.ValidFrom))
            {
                if (HorseSlug.From(name.Name) is not { } slug || taken.ContainsKey(slug)) continue;
                db.RoutingAddresses.Add(RoutingAddress.ForHorse(horse, slug, at));
                taken[slug] = horse.Id;
                added++;
            }
        }

        if (added > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
