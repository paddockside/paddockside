using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Facts, corrections, replies, deliveries and the stage strip.</summary>
public sealed class StreamTests
{
    [Fact]
    public void A_correction_supersedes_the_fact_and_leaves_the_original_untouched()
    {
        var s = new Stable();
        var raceStart = new Event(s.Horse, EventTypes.RaceStart.Code, "Randwick R6", On(2026, 10, 17));
        var acceptance = StreamItem.Fact(s.Horse, raceStart, "Acceptance", "Racing NSW",
            [new("Barrier", "7"), new("Weight", "56.5 kg")], On(2026, 10, 14), stepCode: "ACC_FACT");

        var corrected = acceptance.Correct(s.Horse, raceStart, [new("Barrier", "9"), new("Weight", "56.5 kg")], On(2026, 10, 14).AddHours(5), "Barrier redraw");

        Assert.Equal(acceptance.Id, corrected.SupersedesId);
        Assert.Equal("Barrier redraw", corrected.CorrectionNote);
        Assert.Equal(("Acceptance", "Racing NSW", "ACC_FACT"), (corrected.Title, corrected.Source, corrected.StepCode));
        Assert.Equal("7", acceptance.Fields.Single(f => f.Label == "Barrier").Value);
        Assert.Equal("9", corrected.Fields.Single(f => f.Label == "Barrier").Value);
    }

    [Fact]
    public void Only_a_fact_can_be_corrected()
    {
        var s = new Stable();
        var note = StreamItem.Note(s.Horse, null, "Rang the vet", On(2026, 10, 1), "Kate");

        Assert.Throws<DomainException>(() => note.Correct(s.Horse, null, [new("x", "y")], On(2026, 10, 2)));
    }

    [Fact]
    public void An_owners_reply_is_visible_to_that_owner_and_staff_but_never_to_other_owners()
    {
        var s = new Stable();
        var margaret = s.Owner("Margaret", 500);
        var tom = s.Owner("Tom", 500);
        var message = StreamItem.Message(s.Horse, null, "Owners tickets", "How many tickets?", StreamItemScope.Owners, On(2025, 4, 1), "Kate");

        var reply = message.Reply(s.Horse, null, "Margaret", margaret.Id, "email", "Two please", On(2025, 4, 2));

        Assert.Equal(message.Id, reply.InReplyToId);
        Assert.True(s.CanSee(margaret, reply));
        Assert.False(s.CanSee(tom, reply));
        Assert.True(s.StaffCanSee(reply));
        Assert.Throws<DomainException>(() => reply.Reply(s.Horse, null, "Tom", tom.Id, "email", "Replying to a reply", On(2025, 4, 3)));
    }

    [Fact]
    public void A_reply_from_an_unknown_sender_stays_internal()
    {
        var s = new Stable();
        var owner = s.Owner("Ann", 1000);
        var message = StreamItem.Message(s.Horse, null, null, "Update", StreamItemScope.Owners, On(2025, 4, 1), "Kate");

        var reply = message.Reply(s.Horse, null, "someone@example.com", null, "email", "Who is this?", On(2025, 4, 2));

        Assert.Equal(StreamItemScope.Internal, reply.Scope);
        Assert.False(s.CanSee(owner, reply));
    }

    [Fact]
    public void Deliveries_go_to_current_owners_only_and_start_queued()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var dee = s.Owner("Dee", 500);
        s.Horse.TransferInterest(s.ActiveInterestOf(dee).Id, On(2026, 7, 1), [(s.NewParty("Eve"), 500)]);
        var message = StreamItem.Message(s.Horse, null, null, "Barrier 9", StreamItemScope.Owners, On(2026, 10, 14), "Kate");

        var deliveries = Delivery.ForOwners(s.Tenant, s.Horse, message, DeliveryChannel.Email, On(2026, 10, 14));

        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.PartyId == ann.Id);
        Assert.DoesNotContain(deliveries, d => d.PartyId == dee.Id);
        Assert.All(deliveries, d => Assert.Equal(DeliveryStatus.Queued, d.Status));
    }

    [Fact]
    public void Delivery_status_moves_forward_or_to_a_failure()
    {
        var s = new Stable();
        s.Owner("Ann", 1000);
        var message = StreamItem.Message(s.Horse, null, null, "Hi", StreamItemScope.Owners, On(2026, 10, 1), "Kate");
        var delivery = Assert.Single(Delivery.ForOwners(s.Tenant, s.Horse, message, DeliveryChannel.Email, On(2026, 10, 1)));

        delivery.Record(DeliveryStatus.Opened, On(2026, 10, 2));
        delivery.Record(DeliveryStatus.Delivered, On(2026, 10, 2)); // late report, ignored
        Assert.Equal(DeliveryStatus.Opened, delivery.Status);

        delivery.Record(DeliveryStatus.Bounced, On(2026, 10, 3));
        Assert.Equal(DeliveryStatus.Bounced, delivery.Status);
    }

    [Fact]
    public void Stages_mark_done_missing_current_and_upcoming()
    {
        var raceDay = On(2026, 10, 17);
        var now = On(2026, 10, 15).AddHours(12);

        var stages = EventTypes.Stages(EventTypes.RaceStart, raceDay, ["NOM_FACT", "ACC_FACT", "BARRIER"], now);
        StageState Of(string code) => stages.Single(st => st.Step.Code == code).State;

        Assert.Equal(StageState.Done, Of("NOM_FACT"));
        Assert.Equal(StageState.Missing, Of("TRAINER_PLAN"));   // due 14 days out, never arrived
        Assert.Equal(StageState.Done, Of("BARRIER"));
        Assert.Equal(StageState.Current, Of("TICKETS"));        // due today (15 Oct): not late until tomorrow
        Assert.Equal(StageState.Upcoming, Of("RESULT_FACT"));
        Assert.Single(stages, st => st.State == StageState.Current);
    }

    [Fact]
    public void A_step_becomes_missing_the_day_after_it_was_due()
    {
        var raceDay = On(2026, 10, 17);

        var stages = EventTypes.Stages(EventTypes.RaceStart, raceDay, ["NOM_FACT", "TRAINER_PLAN", "ACC_FACT", "BARRIER"], On(2026, 10, 16).AddHours(9));

        Assert.Equal(StageState.Missing, stages.Single(st => st.Step.Code == "TICKETS").State);   // due 15 Oct
        Assert.Equal(StageState.Current, stages.Single(st => st.Step.Code == "TRAINER_PRE").State); // due 16 Oct
    }

    [Fact]
    public void A_finished_event_has_no_current_step()
    {
        var stages = EventTypes.Stages(EventTypes.BarrierTrial, null, ["TRIAL_ENTRY"], On(2026, 10, 1), finished: true);

        Assert.DoesNotContain(stages, st => st.State == StageState.Current);
    }

    [Theory]
    [InlineData(2022, 9, 1, 2026, 10, 5, 4)]   // foaled in spring 2022: a 4-year-old by October 2026
    [InlineData(2022, 9, 1, 2026, 7, 31, 3)]   // still 3 until 1 August
    [InlineData(2025, 10, 1, 2026, 10, 5, 1)]  // a yearling
    public void Age_counts_from_1_August(int fy, int fm, int fd, int y, int m, int d, int expected)
    {
        var s = new Stable();
        s.Horse.SetDetails(HorseSex.Mare, new DateOnly(fy, fm, fd), "Sovereign Star", "Miss Finland");

        Assert.Equal(expected, s.Horse.AgeOn(new DateOnly(y, m, d)));
    }
}
