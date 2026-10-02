namespace Paddockside.Domain.Tests;

/// <summary>
/// A tenant with one managed horse, bought on <see cref="Purchased"/> and held behind a single managed
/// syndicate line. Keeps the scenario tests reading like ownership-model.md §4.
/// </summary>
internal sealed class Stable
{
    public static readonly DateTimeOffset Purchased = On(2024, 3, 1);

    public Stable()
    {
        Tenant = new Tenant("Laurel Oak Bloodstock");
        Horse = new Horse(Tenant.Id, "Faultless Miss", HorseNameKind.Registered, Purchased);
        Horse.OpenManagementPeriod(Purchased);
        Syndicate = new HoldingEntity(Tenant.Id, "Laurel Oak Bloodstock", HoldingEntityType.ManagedSyndicate);
    }

    public Tenant Tenant { get; }

    public Horse Horse { get; }

    public HoldingEntity Syndicate { get; }

    public static DateTimeOffset On(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.FromHours(10));

    public Party NewParty(string name) => new(Tenant.Id, name);

    /// <summary>A new party holding <paramref name="units"/> behind the syndicate line.</summary>
    public Party Owner(string name, long units, DateTimeOffset? from = null)
    {
        var party = NewParty(name);
        Horse.AddInterest(party, Syndicate, units, from ?? Purchased);
        return party;
    }

    public ManagedInterest ActiveInterestOf(Party party) => Horse.Interests.Single(i => i.PartyId == party.Id && i.IsActive);

    public StreamItem Item(DateTimeOffset at, StreamItemScope scope = StreamItemScope.Owners, params Party[] named) =>
        new(Horse, null, StreamItemKind.Message, scope, StreamItemDirection.Outbound, at, $"{scope} update",
            namedPartyIds: named.Select(p => p.Id));

    public bool CanSee(Party party, StreamItem item) => AccessRule.CanSee(Viewer.Client(party), Tenant, Horse, item);

    public bool StaffCanSee(StreamItem item) => AccessRule.CanSee(Viewer.Staff(Tenant.Id), Tenant, Horse, item);

    public IReadOnlySet<Guid> Audience(StreamItem item) => AccessRule.CurrentOwnerAudience(Tenant, Horse, item);

    public static void AssertParties(IEnumerable<Guid> actual, params Party[] expected) =>
        Assert.Equal(expected.Select(p => p.Id).OrderBy(id => id), actual.OrderBy(id => id));
}
