using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>The scope rule and the edges of the access rule (ownership-model.md §3, tenant-model.md §3).</summary>
public sealed class AccessRuleTests
{
    [Theory]
    [InlineData(StreamItemScope.Internal)]
    [InlineData(StreamItemScope.Trainer)]
    public void Owners_never_see_internal_or_trainer_items_but_staff_do(StreamItemScope scope)
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 1000);
        var item = s.Item(On(2025, 4, 12), scope);

        Assert.False(s.CanSee(ann, item));
        Assert.Empty(s.Audience(item));
        Assert.True(s.StaffCanSee(item));
    }

    [Fact]
    public void Named_parties_item_reaches_only_the_named_current_owners()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var ben = s.Owner("Ben", 500);
        var stranger = s.NewParty("Not an owner");
        var item = s.Item(On(2025, 4, 12), StreamItemScope.NamedParties, ann, stranger);

        Assert.True(s.CanSee(ann, item));
        Assert.False(s.CanSee(ben, item));
        Assert.False(s.CanSee(stranger, item));
        AssertParties(s.Audience(item), ann);
    }

    [Fact]
    public void Pending_interest_gives_no_access_until_it_is_active()
    {
        var s = new Stable();
        var kit = s.NewParty("Kit");
        var interest = s.Horse.AddInterest(kit, s.Syndicate, 250, On(2026, 7, 1), initialState: InterestState.Pending);
        var item = s.Item(On(2025, 4, 12));

        Assert.False(s.CanSee(kit, item));

        s.Horse.ActivateInterest(interest.Id);
        Assert.True(s.CanSee(kit, item));
    }

    [Fact]
    public void Retained_read_only_exit_keeps_sight_of_the_owners_own_period_only()
    {
        var s = new Stable();
        var dee = s.Owner("Dee", 1000);
        var during = s.Item(On(2025, 4, 12));
        var negotiation = s.Item(On(2026, 6, 20), StreamItemScope.OwnersAtTheTime);

        s.Horse.TransferInterest(s.ActiveInterestOf(dee).Id, On(2026, 7, 1), [(s.NewParty("Eve"), 1000)], AccessPolicy.RetainedReadOnly);
        var after = s.Item(On(2026, 8, 2));

        Assert.True(s.CanSee(dee, during));
        Assert.True(s.CanSee(dee, negotiation));
        Assert.False(s.CanSee(dee, after));
        Assert.DoesNotContain(dee.Id, s.Audience(during));
    }

    [Fact]
    public void Nobody_sees_across_tenants()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 1000);
        var item = s.Item(On(2025, 4, 12));

        var other = new Tenant("Another syndicator");
        var otherStaff = Viewer.Staff(other.Id);
        var otherClient = Viewer.Client(new Party(other.Id, "Ann"));

        Assert.False(AccessRule.CanSee(otherStaff, s.Tenant, s.Horse, item));
        Assert.False(AccessRule.CanSee(otherStaff, other, s.Horse, item));
        Assert.False(AccessRule.CanSee(otherClient, s.Tenant, s.Horse, item));
        Assert.False(AccessRule.CanSee(new Viewer(other.Id, AudienceClass.Client, ann.Id), other, s.Horse, item));
    }

    [Fact]
    public void Suppliers_and_partners_are_denied_until_party_roles_exist()
    {
        var s = new Stable();
        var trainer = s.NewParty("Trainer");
        var item = s.Item(On(2025, 4, 12), StreamItemScope.Trainer);

        Assert.False(AccessRule.CanSee(new Viewer(s.Tenant.Id, AudienceClass.Supplier, trainer.Id), s.Tenant, s.Horse, item));
        Assert.False(AccessRule.CanSee(new Viewer(s.Tenant.Id, AudienceClass.Partner), s.Tenant, s.Horse, item));
    }
}
