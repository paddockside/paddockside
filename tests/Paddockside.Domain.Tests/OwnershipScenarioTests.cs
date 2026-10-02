using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>The scenarios in ownership-model.md §4, one test each.</summary>
public sealed class OwnershipScenarioTests
{
    [Fact]
    public void Scenario1_exiting_owner_is_locked_out_and_the_remaining_three_absorb_the_share()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 250);
        var ben = s.Owner("Ben", 250);
        var cal = s.Owner("Cal", 250);
        var dee = s.Owner("Dee", 250);
        var raceday = s.Item(On(2025, 4, 12));

        var exitDay = On(2026, 6, 30);
        var exited = s.ActiveInterestOf(dee);
        var absorbed = s.Horse.TransferInterest(exited.Id, exitDay, [(ann, 84), (ben, 83), (cal, 83)]);
        var afterExit = s.Item(On(2026, 7, 5));

        // The exiting owner is locked out immediately — from the history they paid for and everything after.
        Assert.False(s.CanSee(dee, raceday));
        Assert.False(s.CanSee(dee, afterExit));
        Assert.Equal(InterestState.Exited, exited.State);
        Assert.Equal(exitDay, exited.EffectiveTo);
        Assert.Equal(AccessPolicy.LockedOut, exited.AccessPolicy);

        // The remaining owners carry on seeing everything.
        foreach (var owner in new[] { ann, ben, cal })
        {
            Assert.True(s.CanSee(owner, raceday));
            Assert.True(s.CanSee(owner, afterExit));
        }

        // The new interests trace back to the exited one, and no units are lost.
        Assert.All(absorbed, i => Assert.Equal(exited.Id, i.DerivedFromId));
        Assert.Equal(1000L, s.Horse.Interests.Where(i => i.IsActive).Sum(i => i.Units));

        // Tonight's send list no longer includes them; staff still see the full history.
        AssertParties(s.Audience(afterExit), ann, ben, cal);
        Assert.True(s.StaffCanSee(raceday));
    }

    [Fact]
    public void Scenario2_incoming_owners_see_full_history_except_items_for_owners_at_the_time()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var dee = s.Owner("Dee", 500);
        var maidenWin = s.Item(On(2024, 10, 19));
        var negotiation = s.Item(On(2026, 6, 20), StreamItemScope.OwnersAtTheTime);

        var sold = On(2026, 7, 1);
        var bought = s.Horse.TransferInterest(s.ActiveInterestOf(dee).Id, sold,
            [(s.NewParty("Eve"), 250), (s.NewParty("Fin"), 250)]);
        var eve = bought[0].PartyId;
        var nextStart = s.Item(On(2026, 8, 2));

        // Incoming owners see the complete history back to purchase, long before they bought in...
        Assert.All(bought, i => Assert.True(AccessRule.CanSee(new Viewer(s.Tenant.Id, AudienceClass.Client, i.PartyId), s.Tenant, s.Horse, maidenWin)));
        Assert.Contains(eve, s.Audience(nextStart));

        // ...except the negotiation that led to them buying in.
        Assert.DoesNotContain(eve, s.Audience(negotiation));
        Assert.True(s.CanSee(ann, negotiation));

        // The seller sees none of it.
        Assert.False(s.CanSee(dee, maidenWin));
        Assert.False(s.CanSee(dee, negotiation));
        Assert.All(bought, i => Assert.Equal(sold, i.EffectiveFrom));
    }

    [Fact]
    public void Scenario3a_complete_change_in_house_gives_the_new_group_the_whole_period()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var ben = s.Owner("Ben", 500);
        var raceday = s.Item(On(2025, 4, 12));
        var oldGroupsBusiness = s.Item(On(2026, 5, 1), StreamItemScope.OwnersAtTheTime);

        var changeover = On(2026, 7, 1);
        var gus = s.NewParty("Gus");
        var hal = s.NewParty("Hal");
        s.Horse.TransferInterest(s.ActiveInterestOf(ann).Id, changeover, [(gus, 500)]);
        s.Horse.TransferInterest(s.ActiveInterestOf(ben).Id, changeover, [(hal, 500)]);

        // The horse never left: still one management period, and the new group inherits it.
        Assert.Single(s.Horse.ManagementPeriods);
        Assert.True(s.CanSee(gus, raceday));
        Assert.True(s.CanSee(hal, raceday));
        Assert.False(s.CanSee(gus, oldGroupsBusiness));

        Assert.False(s.CanSee(ann, raceday));
        Assert.False(s.CanSee(ben, raceday));
    }

    [Fact]
    public void Scenario3b_sold_to_a_third_party_ends_access_for_everyone_but_the_horse_record_persists()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var ben = s.Owner("Ben", 500);
        var raceday = s.Item(On(2025, 4, 12));

        s.Horse.CloseManagementPeriod(On(2027, 2, 1), "Sold to a third party");

        Assert.Null(s.Horse.CurrentManagementPeriod);
        Assert.All(s.Horse.Interests, i => Assert.Equal(InterestState.Exited, i.State));
        Assert.False(s.CanSee(ann, raceday));
        Assert.False(s.CanSee(ben, raceday));
        Assert.Empty(s.Audience(raceday));
        Assert.True(s.StaffCanSee(raceday));
    }

    [Fact]
    public void Scenario3b_horse_returning_under_a_new_group_does_not_expose_the_old_period()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 1000);
        var raceday = s.Item(On(2025, 4, 12));
        var oldGroupsBusiness = s.Item(On(2026, 5, 1), StreamItemScope.OwnersAtTheTime);
        s.Horse.CloseManagementPeriod(On(2027, 2, 1), "Sold to a third party");

        // Bought back years later as a broodmare. Ann buys back in alongside a new owner.
        var returned = On(2030, 8, 1);
        s.Horse.OpenManagementPeriod(returned);
        var jo = s.Owner("Jo", 600, returned);
        s.Horse.AddInterest(ann, s.Syndicate, 400, returned);
        var coveringBooked = s.Item(On(2030, 9, 10));

        Assert.Equal(2, s.Horse.ManagementPeriods.Count);
        Assert.True(s.CanSee(jo, coveringBooked));
        Assert.True(s.CanSee(ann, coveringBooked));

        // By default (D10) nobody in the new group sees the previous period — not even a returning owner.
        Assert.False(s.CanSee(jo, raceday));
        Assert.False(s.CanSee(ann, raceday));

        // A tenant can opt in to showing it; "owners at the time" items still stay with their own owners.
        s.Tenant.PriorManagementPeriodsVisible = true;
        Assert.True(s.CanSee(jo, raceday));
        Assert.False(s.CanSee(jo, oldGroupsBusiness));
        Assert.True(s.CanSee(ann, oldGroupsBusiness));
    }

    [Fact]
    public void Scenario4_part_owned_horse_keeps_registered_co_owners_out_of_every_audience()
    {
        var s = new Stable();
        var house = s.Owner("Laurel Oak house share", 100);
        var clients = Enumerable.Range(1, 6).Select(n => s.Owner($"Client {n}", 150)).ToArray();

        // Other names in the official record: external lines, display only.
        var rosehill = new HoldingEntity(s.Tenant.Id, "Rosehill Racing P/S", HoldingEntityType.External);
        var corrigan = new HoldingEntity(s.Tenant.Id, "M. J. Corrigan", HoldingEntityType.External);
        Assert.False(rosehill.ExpandsInternally);
        Assert.False(corrigan.ExpandsInternally);

        // They cannot be given a managed interest, so they can never reach a distribution list.
        var outsider = s.NewParty("M. J. Corrigan");
        var ex = Assert.Throws<DomainException>(() => s.Horse.AddInterest(outsider, corrigan, 250, Purchased));
        Assert.Contains("external", ex.Message);

        // One syndicate line expands into the seven managed interests, and only they are told anything.
        var update = s.Item(On(2025, 4, 12));
        Assert.Equal(7, s.Horse.Interests.Count);
        Assert.All(s.Horse.Interests, i => Assert.Equal(s.Syndicate.Id, i.HoldingEntityId));
        AssertParties(s.Audience(update), [house, .. clients]);
        Assert.False(s.CanSee(outsider, update));
    }
}
