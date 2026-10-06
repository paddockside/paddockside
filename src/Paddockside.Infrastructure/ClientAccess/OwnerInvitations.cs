using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.ClientAccess;

/// <summary>
/// Queues the invitation when a party first gains a managed interest (identity-access.md §6), if the tenant's
/// "invite owners on first interest" setting is on. Runs inside SaveChanges, so however an interest is created —
/// a purchase, a transfer, an import — the rule holds. Sending happens later, off the request.
/// </summary>
public static class OwnerInvitations
{
    public static async Task<int> QueueAsync(PaddocksideDbContext db, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (NewInterests(db) is not { Count: > 0 } added) return 0;
        var tenant = TrackedTenant(db) ?? await db.Tenants.SingleOrDefaultAsync(cancellationToken);
        if (tenant is not { InviteOwnersOnFirstInterest: true }) return 0;

        var partyIds = added.Select(i => i.PartyId).Distinct().ToList();
        var earlier = await db.Set<ManagedInterest>().Where(i => partyIds.Contains(i.PartyId)).Select(i => i.PartyId).Distinct().ToListAsync(cancellationToken);
        var invited = await db.OwnerInvitations.Where(i => partyIds.Contains(i.PartyId)).Select(i => i.PartyId).ToListAsync(cancellationToken);
        var linked = await db.Parties.Where(p => partyIds.Contains(p.Id) && p.PersonId != null).Select(p => p.Id).ToListAsync(cancellationToken);
        return Queue(db, tenant, added, [.. earlier, .. invited, .. linked], at);
    }

    public static int Queue(PaddocksideDbContext db, DateTimeOffset at)
    {
        if (NewInterests(db) is not { Count: > 0 } added) return 0;
        var tenant = TrackedTenant(db) ?? db.Tenants.SingleOrDefault();
        if (tenant is not { InviteOwnersOnFirstInterest: true }) return 0;

        var partyIds = added.Select(i => i.PartyId).Distinct().ToList();
        var earlier = db.Set<ManagedInterest>().Where(i => partyIds.Contains(i.PartyId)).Select(i => i.PartyId).Distinct().ToList();
        var invited = db.OwnerInvitations.Where(i => partyIds.Contains(i.PartyId)).Select(i => i.PartyId).ToList();
        var linked = db.Parties.Where(p => partyIds.Contains(p.Id) && p.PersonId != null).Select(p => p.Id).ToList();
        return Queue(db, tenant, added, [.. earlier, .. invited, .. linked], at);
    }

    private static int Queue(PaddocksideDbContext db, Tenant tenant, List<ManagedInterest> added, HashSet<Guid> skip, DateTimeOffset at)
    {
        // Invitations already added in this unit of work, and parties linked in it, count too.
        foreach (var pending in db.ChangeTracker.Entries<OwnerInvitation>()) skip.Add(pending.Entity.PartyId);
        foreach (var party in db.ChangeTracker.Entries<Party>().Where(p => p.Entity.PersonId != null)) skip.Add(party.Entity.Id);

        var queued = 0;
        foreach (var interest in added.OrderBy(i => i.EffectiveFrom))
        {
            if (!skip.Add(interest.PartyId)) continue;
            db.OwnerInvitations.Add(new OwnerInvitation(tenant.Id, interest.PartyId, interest.HorseId, at));
            queued++;
        }

        return queued;
    }

    private static List<ManagedInterest> NewInterests(PaddocksideDbContext db) =>
        db.ChangeTracker.Entries<ManagedInterest>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();

    private static Tenant? TrackedTenant(PaddocksideDbContext db) =>
        db.ChangeTracker.Entries<Tenant>().Select(e => e.Entity).FirstOrDefault();
}
