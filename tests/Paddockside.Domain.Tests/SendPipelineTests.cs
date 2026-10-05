using static Paddockside.Domain.Tests.Stable;

namespace Paddockside.Domain.Tests;

/// <summary>Routing tokens and who a message goes to (messaging-channels.md §1–§2).</summary>
public sealed class SendPipelineTests
{
    [Fact]
    public void Routing_tokens_are_12_characters_from_the_unambiguous_alphabet_and_do_not_repeat()
    {
        var tokens = Enumerable.Range(0, 20_000).Select(_ => RoutingToken.New()).ToList();

        Assert.All(tokens, t =>
        {
            Assert.Equal(12, t.Length);
            Assert.True(RoutingToken.IsWellFormed(t), t);
            Assert.DoesNotContain(t, c => "01ilo".Contains(c) || char.IsUpper(c));
        });
        Assert.Equal(tokens.Count, tokens.Distinct().Count());

        // Every character of the alphabet turns up: the generator is not stuck on part of it.
        Assert.Equal(RoutingToken.Alphabet.OrderBy(c => c), tokens.SelectMany(t => t).Distinct().OrderBy(c => c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghjkmn2")]    // 13 characters
    [InlineData("abcdefghjkm0")]     // 0 is not in the alphabet
    [InlineData("ABCDEFGHJKMN")]     // upper case
    public void Malformed_tokens_are_rejected(string token) => Assert.False(RoutingToken.IsWellFormed(token));

    [Fact]
    public void An_owner_who_exited_this_morning_is_not_a_recipient_of_tonights_message()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 500);
        var dee = s.Owner("Dee", 500);
        var thisMorning = On(2026, 10, 6).AddHours(6);
        var tonight = On(2026, 10, 6).AddHours(19);

        s.Horse.TransferInterest(s.ActiveInterestOf(dee).Id, thisMorning, [(ann, 500)]);
        var update = StreamItem.Message(s.Horse, null, "Barrier draw", "Barrier 9.", StreamItemScope.Owners, tonight, "Kate");

        var recipients = Delivery.ForOwners(s.Tenant, s.Horse, update, DeliveryChannel.Email, tonight);

        Assert.Equal(ann.Id, Assert.Single(recipients).PartyId);
        Assert.False(s.CanSee(dee, update));
    }

    [Fact]
    public void A_recipient_token_renders_as_the_tenants_inbound_reply_address()
    {
        var s = new Stable();
        var ann = s.Owner("Ann", 1000);
        s.Tenant.SetBranding("laureloak", null, null);
        var message = StreamItem.Message(s.Horse, null, null, "Hello", StreamItemScope.Owners, On(2026, 10, 6), "Kate");

        var address = RoutingAddress.ForRecipient(message, ann.Id, "abcdefghjkmn", On(2026, 10, 6));

        Assert.Equal("r-abcdefghjkmn@laureloak.in.paddockside.com.au", address.EmailAddress(s.Tenant, "in.paddockside.com.au"));
        Assert.Equal((message.Id, message.HorseId, ann.Id), (address.StreamItemId!.Value, address.HorseId, address.PartyId!.Value));
    }

    [Fact]
    public void Tenant_slugs_keep_only_lowercase_letters_and_digits()
    {
        Assert.Equal("laureloakbloodstockdemo", new Tenant("Laurel Oak Bloodstock (demo)").Slug);
        Assert.Equal("laureloak", new Tenant("Laurel Oak Bloodstock", "Laurel-Oak").Slug);
        Assert.Throws<DomainException>(() => new Tenant("Name", "!!!"));
    }

    [Fact]
    public void An_undeliverable_address_stays_flagged_until_cleared()
    {
        var s = new Stable();
        var ann = s.NewParty("Ann Lee");
        ann.AddEmail("Ann@Example.test");

        ann.MarkUndeliverable("ann@example.test", On(2026, 10, 6));
        Assert.False(ann.PrimaryEmail!.IsDeliverable);

        ann.ClearUndeliverable("ANN@example.test");
        Assert.True(ann.PrimaryEmail!.IsDeliverable);
        Assert.Equal("Ann", ann.FirstName);
    }

    [Fact]
    public void A_delivery_records_the_send_and_failures_honestly()
    {
        var s = new Stable();
        s.Owner("Ann", 1000);
        var message = StreamItem.Message(s.Horse, null, null, "Hello", StreamItemScope.Owners, On(2026, 10, 6), "Kate");
        var delivery = Assert.Single(Delivery.ForOwners(s.Tenant, s.Horse, message, DeliveryChannel.Email, On(2026, 10, 6)));

        delivery.RecordFailedAttempt("Postmark unavailable", permanent: false, On(2026, 10, 6));
        Assert.True(delivery.IsWaitingToSend);
        Assert.Equal(1, delivery.Attempts);

        delivery.MarkSent("ann@example.test", "pm-123", On(2026, 10, 6).AddMinutes(1));
        Assert.Equal((DeliveryStatus.Sent, "pm-123", 2), (delivery.Status, delivery.ProviderMessageId, delivery.Attempts));
        Assert.Throws<DomainException>(() => delivery.MarkSent("ann@example.test", "pm-124", On(2026, 10, 6)));
    }
}
