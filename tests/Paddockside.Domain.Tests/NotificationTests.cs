using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Notification categories and how an owner's choices become channels (messaging-channels.md §6).</summary>
public sealed class NotificationTests
{
    private static readonly NotificationCategory Results = NotificationCategories.Find(NotificationCategories.Results)!;
    private static readonly NotificationCategory StableUpdates = NotificationCategories.Find(NotificationCategories.StableUpdates)!;

    private static NotificationPreference Choice(Party party, NotificationCategory category, DeliveryChannel channel, bool on) =>
        new(party, category.Code, channel, on, On(2026, 10, 1));

    [Fact]
    public void Defaults_apply_until_the_owner_chooses_and_only_reachable_ways_are_used()
    {
        var owner = new Party(Guid.NewGuid(), "Ann");

        // Results default to email and text; with texts not yet available, email only.
        Assert.Equal([DeliveryChannel.Email], NotificationCategories.ChannelsFor(Results, [], hasEmail: true, hasMobile: true, smsAvailable: false));
        Assert.Equal([DeliveryChannel.Email, DeliveryChannel.Sms],
            NotificationCategories.ChannelsFor(Results, [], hasEmail: true, hasMobile: true, smsAvailable: true).OrderBy(c => c));

        // Their choice wins over the default.
        Assert.Equal([DeliveryChannel.Sms],
            NotificationCategories.ChannelsFor(Results, [Choice(owner, Results, DeliveryChannel.Email, false)], true, true, smsAvailable: true));
    }

    [Fact]
    public void An_important_category_always_reaches_them_some_way()
    {
        var owner = new Party(Guid.NewGuid(), "Ann");
        var emailOff = Choice(owner, Results, DeliveryChannel.Email, false);

        // Text only, but texts aren't available yet: back to email rather than silence.
        Assert.Equal([DeliveryChannel.Email], NotificationCategories.ChannelsFor(Results, [emailOff], true, true, smsAvailable: false));

        // No email address at all: a text, when it can be sent.
        Assert.Equal([DeliveryChannel.Sms], NotificationCategories.ChannelsFor(Results, [], hasEmail: false, hasMobile: true, smsAvailable: true));
    }

    [Fact]
    public void An_optional_category_switched_off_sends_nothing()
    {
        var owner = new Party(Guid.NewGuid(), "Ann");
        Assert.Empty(NotificationCategories.ChannelsFor(StableUpdates, [Choice(owner, StableUpdates, DeliveryChannel.Email, false)], true, true, true));
    }

    [Theory]
    [InlineData("RaceStart", "ACC_FACT", NotificationCategories.Acceptances)]
    [InlineData("RaceStart", "BARRIER", NotificationCategories.Acceptances)]
    [InlineData("RaceStart", "TICKETS", NotificationCategories.RaceDayArrangements)]
    [InlineData("RaceStart", "RESULT_FACT", NotificationCategories.Results)]
    [InlineData("RaceStart", "NOM_FACT", NotificationCategories.Entries)]
    [InlineData("RaceStart", null, NotificationCategories.StableUpdates)]
    [InlineData("Veterinary", null, NotificationCategories.SalesBreedingCare)]
    public void A_message_takes_its_category_from_its_step(string eventType, string? step, string expected)
    {
        var s = new Stable();
        var e = new Event(s.Horse, eventType, "Test", On(2026, 10, 10));
        var message = StreamItem.Message(s.Horse, e, null, "Hello", StreamItemScope.Owners, On(2026, 10, 1), "Kate", stepCode: step);
        Assert.Equal(expected, NotificationCategories.For(message, e).Code);
    }

    [Fact]
    public void Preferences_are_only_for_known_categories_and_email_or_text()
    {
        var owner = new Party(Guid.NewGuid(), "Ann");
        Assert.Throws<DomainException>(() => new NotificationPreference(owner, "made-up", DeliveryChannel.Email, true, On(2026, 10, 1)));
        Assert.Throws<DomainException>(() => new NotificationPreference(owner, NotificationCategories.Results, DeliveryChannel.Portal, false, On(2026, 10, 1)));
    }
}
