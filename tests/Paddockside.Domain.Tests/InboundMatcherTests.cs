using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Tier 3: what the matcher makes of mail to a general address (data-model.md §3, messaging-channels.md §3.3).</summary>
public sealed class InboundMatcherTests
{
    private static readonly DateTimeOffset Now = On(2026, 10, 14);

    private sealed class Yard
    {
        public Tenant Tenant { get; } = new("Laurel Oak");

        public HoldingEntity Syndicate { get; }

        public Yard() => Syndicate = new HoldingEntity(Tenant.Id, "Syndicate", HoldingEntityType.ManagedSyndicate);

        public List<MatchHorse> Horses { get; } = [];

        public Horse Add(string name, string? dam = null, HorseNameKind kind = HorseNameKind.Registered, Party? owner = null, Event? openRace = null)
        {
            var horse = new Horse(Tenant.Id, name, kind, On(2024, 1, 1));
            horse.SetDetails(null, null, "Sovereign Star", dam);
            horse.OpenManagementPeriod(On(2024, 1, 1));
            if (owner is not null) horse.AddInterest(owner, Syndicate, 500, On(2024, 1, 1));
            Horses.Add(new MatchHorse(horse, [], new Dictionary<Guid, DateTimeOffset>()));
            return horse;
        }

        public Event Race(Horse horse, string title, DateTimeOffset day, DateTimeOffset? lastActivity = null)
        {
            var race = new Event(horse, EventTypes.RaceStart.Code, title, day);
            race.Open(day.AddDays(-10));
            var index = Horses.FindIndex(h => h.Horse.Id == horse.Id);
            var existing = Horses[index];
            var activity = new Dictionary<Guid, DateTimeOffset>(existing.LastActivity);
            if (lastActivity is { } at) activity[race.Id] = at;
            Horses[index] = existing with { OpenEvents = [.. existing.OpenEvents, race], LastActivity = activity };
            return race;
        }

        public IReadOnlyList<MatchSuggestion> Score(string subject, string body, string? local = "office", Guid? sender = null) =>
            InboundMatcher.Score(subject, body, local, sender, Horses, Now);
    }

    [Fact]
    public void A_horse_named_in_the_subject_with_its_race_in_window_is_a_clear_suggestion()
    {
        var yard = new Yard();
        var faultless = yard.Add("Faultless Miss");
        yard.Add("Northern Lark");
        var race = yard.Race(faultless, "Randwick, Race 6", Now.AddDays(3), lastActivity: Now.AddDays(-1));

        var scored = yard.Score("Faultless Miss's gear for Randwick", "Blinkers on.");
        var winner = InboundMatcher.Winner(scored);
        Assert.NotNull(winner);
        Assert.Equal((faultless.Id, race.Id), (winner.HorseId, winner.EventId));
        Assert.Contains("Faultless Miss named in the subject", winner.Reasons);
        Assert.Contains("\"Randwick\" mentioned", winner.Reasons);
        Assert.Contains("Recent activity on it", winner.Reasons);
    }

    [Fact]
    public void An_unnamed_horse_is_found_by_its_dam_and_a_horse_by_its_address_hint()
    {
        var yard = new Yard();
        var lot = yard.Add("Lot 231 (unnamed)", dam: "Miss Finland", kind: HorseNameKind.SaleLot);
        var lark = yard.Add("Northern Lark", dam: "Lark Song");

        Assert.Equal(lot.Id, yard.Score("Easter", "Is the one out of Miss Finland still available?").First().HorseId);
        Assert.Equal(lot.Id, yard.Score("Inglis", "Lot 231 vetted fine.").First().HorseId);
        Assert.Contains("Sent to northern-lark@, which looks like Northern Lark",
            yard.Score("Update", "All good.", local: "northern-lark").Single().Reasons);
        _ = lark;
    }

    [Fact]
    public void Two_horses_named_equally_or_nothing_named_leaves_it_pending()
    {
        var yard = new Yard();
        yard.Add("Faultless Miss");
        yard.Add("Northern Lark");

        Assert.Null(InboundMatcher.Winner(yard.Score("Faultless Miss and Northern Lark", "Both worked well.")));
        Assert.Empty(yard.Score("Invoice", "See attached."));
        Assert.Empty(yard.Score("Miss you", "A miss is as good as a mile.")); // part of a name is not the name
    }

    [Fact]
    public void A_known_owner_with_one_horse_points_to_it_but_not_enough_on_its_own()
    {
        var yard = new Yard();
        var ann = new Party(yard.Tenant.Id, "Ann");
        var faultless = yard.Add("Faultless Miss", owner: ann);
        yard.Race(faultless, "Randwick, Race 6", Now.AddDays(3));

        var scored = yard.Score("Quick question", "Can we visit?", sender: ann.Id);
        Assert.Equal(faultless.Id, scored.Single().HorseId);
        Assert.Contains("Sent by an owner whose only horse here this is", scored.Single().Reasons);
        Assert.Null(InboundMatcher.Winner(scored)); // 25 + 20: worth showing, not worth suggesting
    }
}
