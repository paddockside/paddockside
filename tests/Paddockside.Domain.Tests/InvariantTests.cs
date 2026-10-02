using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>The rules the entities refuse to break.</summary>
public sealed class InvariantTests
{
    [Fact]
    public void Transfer_must_pass_on_exactly_the_units_held()
    {
        var s = new Stable();
        var dee = s.Owner("Dee", 250);

        Assert.Throws<DomainException>(() =>
            s.Horse.TransferInterest(s.ActiveInterestOf(dee).Id, On(2026, 7, 1), [(s.NewParty("Eve"), 200)]));
        Assert.True(s.ActiveInterestOf(dee).IsActive);
    }

    [Fact]
    public void Interests_need_an_open_management_period()
    {
        var s = new Stable();
        s.Horse.CloseManagementPeriod(On(2027, 2, 1), "Sold");

        Assert.Throws<DomainException>(() => s.Owner("Late", 100, On(2027, 3, 1)));
        Assert.Throws<DomainException>(() => s.Horse.OpenManagementPeriod(On(2027, 1, 1)));
    }

    [Fact]
    public void Only_one_management_period_is_open_at_a_time()
    {
        var s = new Stable();
        Assert.Throws<DomainException>(() => s.Horse.OpenManagementPeriod(On(2025, 1, 1)));
    }

    [Fact]
    public void Registering_a_name_closes_the_sale_lot_name_without_losing_it()
    {
        var tenant = new Tenant("Laurel Oak Bloodstock");
        var horse = new Horse(tenant.Id, "Snitzel x Faultless '24", HorseNameKind.SaleLot, On(2025, 3, 10), "Inglis Easter Lot 212");
        horse.AddName("Snitz", HorseNameKind.StableName, On(2025, 4, 1));
        horse.AddName("Faultless Miss", HorseNameKind.Registered, On(2025, 9, 1));

        Assert.Equal("Faultless Miss", horse.Name);
        Assert.Equal("Snitzel x Faultless '24", horse.NameAt(On(2025, 6, 1)));
        Assert.Equal(3, horse.Names.Count);
        Assert.True(horse.Names.Single(n => n.Kind == HorseNameKind.StableName).IsCurrent);
    }

    [Fact]
    public void A_note_is_always_internal()
    {
        var s = new Stable();
        Assert.Throws<DomainException>(() =>
            new StreamItem(s.Horse, null, StreamItemKind.Note, StreamItemScope.Owners, StreamItemDirection.Internal, On(2025, 1, 1), "note"));
    }

    [Fact]
    public void A_stream_item_cannot_sit_on_another_horses_event()
    {
        var s = new Stable();
        var otherHorse = new Horse(s.Tenant.Id, "Other", HorseNameKind.Registered, Purchased);
        var otherEvent = new Event(otherHorse, "RaceStart", "Flemington, R4");

        Assert.Throws<DomainException>(() =>
            new StreamItem(s.Horse, otherEvent, StreamItemKind.Message, StreamItemScope.Owners, StreamItemDirection.Outbound, On(2025, 1, 1), "hi"));
    }

    [Fact]
    public void A_closed_event_reopens_when_activity_arrives()
    {
        var s = new Stable();
        var raceStart = new Event(s.Horse, "RaceStart", "Flemington, R4", On(2025, 11, 1));
        raceStart.Open(On(2025, 10, 20));
        raceStart.Close(On(2025, 11, 3), s.NewParty("Kate").Id);

        raceStart.RecordActivity();

        Assert.Equal(EventStatus.Open, raceStart.Status);
        Assert.Null(raceStart.ClosedAt);
    }
}
