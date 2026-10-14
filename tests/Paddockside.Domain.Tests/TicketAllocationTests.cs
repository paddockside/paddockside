using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Owners tickets: who is told, and when confirming is refused.</summary>
public sealed class TicketAllocationTests
{
    [Fact]
    public void Only_owners_whose_number_changed_are_told_and_never_the_house_share()
    {
        var s = new Stable();
        var race = new Event(s.Horse, EventTypes.RaceStart.Code, "Randwick, Race 6", On(2026, 10, 17));
        var (ann, bob, cat, house) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var asked = new Dictionary<Guid, int> { [ann] = 2, [bob] = 3, [cat] = 0 };
        var tickets = new TicketAllocation(race, On(2026, 10, 14));

        tickets.Set(6, [(ann, 2), (bob, 2), (cat, 0), (house, 2)], On(2026, 10, 14));
        Assert.Equal([ann, bob], tickets.ToTell(asked, house).Select(g => g.PartyId)); // Cat asked for none: nothing to say
        tickets.MarkConfirmed([ann, bob], "kate", On(2026, 10, 14));
        Assert.Empty(tickets.ToTell(asked, house));

        // A change: only Bob's number moves, so only Bob is told again. Saving keeps what each was told.
        tickets.Set(6, [(ann, 2), (bob, 1), (cat, 0), (house, 2)], On(2026, 10, 15));
        Assert.Equal([bob], tickets.ToTell(asked, house).Select(g => g.PartyId));
        Assert.Equal(2, tickets.Grants.Single(g => g.PartyId == bob).Confirmed);
    }

    [Fact]
    public void More_given_than_available_cannot_be_confirmed()
    {
        var s = new Stable();
        var race = new Event(s.Horse, EventTypes.RaceStart.Code, "Randwick, Race 6", On(2026, 10, 17));
        var tickets = new TicketAllocation(race, On(2026, 10, 14));
        var ann = Guid.NewGuid();
        tickets.Set(1, [(ann, 2)], On(2026, 10, 14));
        Assert.True(tickets.IsOver);
        Assert.Throws<DomainException>(() => tickets.MarkConfirmed([ann], "kate", On(2026, 10, 14)));

        Assert.Throws<DomainException>(() => tickets.Set(-1, [], On(2026, 10, 14)));
        Assert.Throws<DomainException>(() => tickets.Set(4, [(ann, 1), (ann, 2)], On(2026, 10, 14)));
        Assert.Throws<DomainException>(() => new TicketAllocation(new Event(s.Horse, EventTypes.Veterinary.Code, "Vet", On(2026, 10, 1)), On(2026, 10, 1)));
    }
}
