namespace Paddockside.Domain;

/// <summary>The fixed audience classes (tenant-model.md). Roles live inside them.</summary>
public enum AudienceClass
{
    Staff,
    Client,
    Supplier,
    Partner,
}

/// <summary>Who is asking. Clients and suppliers are identified by their party in this tenant.</summary>
public sealed record Viewer(Guid TenantId, AudienceClass Class, Guid? PartyId = null)
{
    public static Viewer Staff(Guid tenantId) => new(tenantId, AudienceClass.Staff);

    public static Viewer Client(Party party) => new(party.TenantId, AudienceClass.Client, party.Id);
}

/// <summary>
/// The access rule from ownership-model.md §3, as a pure function. This is the third of the three
/// authorisation questions in identity-access.md §5.4 — "does the ownership model give them access to this
/// horse and this item's scope" — and is evaluated the same way for every role.
/// </summary>
public static class AccessRule
{
    /// <summary>
    /// Whether <paramref name="viewer"/> may see <paramref name="item"/>. A client may when all hold:
    /// <list type="number">
    /// <item>they hold an Active managed interest in the horse's current management period (or kept
    /// read-only access to their own period when they exited);</item>
    /// <item>the item's scope includes owners — and for "owners at the time", they held an interest on the
    /// item's date;</item>
    /// <item>the item is not restricted to named parties they are not among.</item>
    /// </list>
    /// Staff see everything in their own tenant. Nobody sees across tenants.
    /// </summary>
    public static bool CanSee(Viewer viewer, Tenant tenant, Horse horse, StreamItem item)
    {
        if (viewer.TenantId != tenant.Id || horse.TenantId != tenant.Id || item.TenantId != tenant.Id || item.HorseId != horse.Id)
            return false;

        return viewer.Class switch
        {
            AudienceClass.Staff => true,
            AudienceClass.Client when viewer.PartyId is Guid partyId => ClientCanSee(partyId, tenant, horse, item),

            // Suppliers do not sign in during v1 (identity-access.md §4.3). Trainer-scoped access arrives with
            // PARTY_ROLE, which knows who trains the horse; until then, deny.
            _ => false,
        };
    }

    /// <summary>
    /// The parties to send <paramref name="item"/> to: current owners the rule admits. Computed at send
    /// time, never stored ahead, so an owner who exited this morning is not on tonight's list.
    /// Read-only retained access never puts someone back on a distribution list.
    /// </summary>
    public static IReadOnlySet<Guid> CurrentOwnerAudience(Tenant tenant, Horse horse, StreamItem item)
    {
        var period = horse.CurrentManagementPeriod;
        if (period is null) return new HashSet<Guid>();

        return horse.Interests
            .Where(i => i.ManagementPeriodId == period.Id && i.IsActive)
            .Select(i => i.PartyId)
            .Where(partyId => ClientCanSee(partyId, tenant, horse, item))
            .ToHashSet();
    }

    private static bool ClientCanSee(Guid partyId, Tenant tenant, Horse horse, StreamItem item)
    {
        // Conditions 2 and 3: does the item's audience include this owner at all?
        var admittedByScope = item.Scope switch
        {
            StreamItemScope.Owners => true,
            StreamItemScope.OwnersAtTheTime => HeldInterestAt(horse, partyId, item.OccurredAt),
            StreamItemScope.NamedParties => item.NamedPartyIds.Contains(partyId),
            _ => false, // Internal, Trainer
        };
        if (!admittedByScope) return false;

        // Condition 1: a live interest now, or read-only access kept on exit.
        return HasLiveAccess(partyId, tenant, horse, item) || HasRetainedAccess(partyId, horse, item);
    }

    /// <summary>
    /// Access follows the current interest, not the dates it was held: an incoming owner sees the whole
    /// current period, back to before they bought in. Earlier periods only if the tenant opts in (D10).
    /// </summary>
    private static bool HasLiveAccess(Guid partyId, Tenant tenant, Horse horse, StreamItem item)
    {
        var period = horse.CurrentManagementPeriod;
        if (period is null) return false;

        var holdsActiveInterest = horse.Interests.Any(i =>
            i.PartyId == partyId && i.ManagementPeriodId == period.Id && i.IsActive);

        return holdsActiveInterest && (tenant.PriorManagementPeriodsVisible || item.OccurredAt >= period.From);
    }

    /// <summary>An exited interest with <see cref="AccessPolicy.RetainedReadOnly"/> keeps sight of its own window.</summary>
    private static bool HasRetainedAccess(Guid partyId, Horse horse, StreamItem item) =>
        horse.Interests.Any(i =>
            i.PartyId == partyId
            && !i.IsActive
            && i.AccessPolicy == AccessPolicy.RetainedReadOnly
            && i.WasHeldAt(item.OccurredAt));

    private static bool HeldInterestAt(Horse horse, Guid partyId, DateTimeOffset at) =>
        horse.Interests.Any(i => i.PartyId == partyId && i.WasHeldAt(at));
}
