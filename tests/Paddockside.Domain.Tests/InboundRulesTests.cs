using Paddockside.Domain;

namespace Paddockside.Domain.Tests;

/// <summary>Reading inbound email (messaging-channels.md §3): addresses, quoted text, automatic mail, attachments, windows.</summary>
public sealed class InboundRulesTests
{
    private const string Inbound = "in.paddockside.com.au";

    [Theory]
    [InlineData("r-abcdefghjkmn@laureloak.in.paddockside.com.au", "laureloak", "abcdefghjkmn", null)]
    [InlineData("R-ABCDEFGHJKMN@LaurelOak.In.Paddockside.com.au", "laureloak", "abcdefghjkmn", null)]
    [InlineData("bel-esprit@laureloak.in.paddockside.com.au", "laureloak", null, "bel-esprit")]
    [InlineData("r-short@laureloak.in.paddockside.com.au", "laureloak", null, "r-short")] // malformed token: no token (§9)
    [InlineData("r-abcdefghjkm0@laureloak.in.paddockside.com.au", "laureloak", null, "r-abcdefghjkm0")] // 0 is not in the alphabet: tier 2 gets a look
    public void Addresses_on_the_inbound_subdomain_are_read_back(string address, string tenant, string? token, string? slug)
    {
        var parsed = InboundAddress.Parse(address, Inbound)!;

        Assert.Equal(tenant, parsed.TenantSlug);
        Assert.Equal(token, parsed.RecipientToken);
        Assert.Equal(slug, parsed.HorseSlugPart);
    }

    [Theory]
    [InlineData("kate@laurel-oak.com.au")]
    [InlineData("x@a.b.in.paddockside.com.au")]
    [InlineData("not-an-address")]
    public void Other_addresses_are_not_ours(string address) => Assert.Null(InboundAddress.Parse(address, Inbound));

    [Fact]
    public void The_tenant_catch_all_has_no_local_part()
    {
        var parsed = InboundAddress.Parse("laureloak@in.paddockside.com.au", Inbound)!;
        Assert.Equal("laureloak", parsed.TenantSlug);
        Assert.Null(parsed.RecipientToken);
        Assert.Null(parsed.HorseSlugPart);
    }

    [Theory]
    [InlineData("Bel Esprit", "bel-esprit")]
    [InlineData("O'Reilly's Pride", "oreillys-pride")]
    [InlineData("  Café   Society!! ", "cafe-society")]
    [InlineData("2026 Inglis Easter Lot 142", "2026-inglis-easter-lot-142")]
    public void Horse_slugs_come_from_the_name(string name, string slug) => Assert.Equal(slug, HorseSlug.From(name));

    [Fact]
    public void A_reply_written_above_gmails_wrapped_quote_header_keeps_only_the_reply()
    {
        var text = "Count me in, and Sam too.\r\n\r\nOn Tue, 6 Oct 2026 at 7:00 pm, Laurel Oak <updates@mail.paddockside.com.au>\r\nwrote:\r\n> Hi Ann,\r\n> Barrier 9 for Saturday.";

        Assert.Equal("Count me in, and Sam too.", QuotedText.Strip(text));
    }

    [Fact]
    public void Outlook_history_and_our_own_template_are_cut()
    {
        var outlook = "Thanks, see you there.\n\nFrom: Laurel Oak <updates@mail.paddockside.com.au>\nSent: Tuesday, 6 October 2026 7:00 PM\nTo: Ann\nSubject: Bel Esprit";
        var bare = "Yes please.\n\nReply to this email to answer. Your reply goes to Laurel Oak only.\nYou are receiving this because you are an owner of Bel Esprit.";

        Assert.Equal("Thanks, see you there.", QuotedText.Strip(outlook));
        Assert.Equal("Yes please.", QuotedText.Strip(bare));
    }

    [Fact]
    public void Interleaved_quotes_and_signatures_go_but_the_reply_around_them_stays()
    {
        var text = "> Can you make Saturday?\nYes.\n> Two tickets?\nThree please.\n-- \nAnn Smith\n0400 000 000";

        Assert.Equal("Yes.\nThree please.", QuotedText.Strip(text));
        Assert.Equal("Great", QuotedText.Strip("Great\n\nSent from my iPhone"));
    }

    [Fact]
    public void A_body_that_is_all_quote_is_shown_whole_rather_than_as_nothing()
    {
        var text = "> just the quote";
        Assert.Equal("> just the quote", QuotedText.Strip(text));
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", "Re: Bel Esprit", "ann@x.test")]
    [InlineData("Precedence", "bulk", "Newsletter", "news@x.test")]
    [InlineData("X-Auto-Response-Suppress", "All", "Hello", "ann@x.test")]
    [InlineData("X-Mailer", "Outlook", "Automatic reply: Bel Esprit", "ann@x.test")]
    [InlineData("X-Mailer", "Outlook", "Out of Office: away until Monday", "ann@x.test")]
    [InlineData("X-Mailer", "Outlook", "Undeliverable: Bel Esprit", "ann@x.test")]
    [InlineData("X-Mailer", "Outlook", "Re: Bel Esprit", "MAILER-DAEMON@x.test")]
    public void Automatic_mail_is_recognised(string header, string value, string subject, string from) =>
        Assert.NotNull(AutomaticMail.Reason([(header, value)], subject, from));

    [Fact]
    public void A_person_writing_is_not_automatic_mail()
    {
        Assert.Null(AutomaticMail.Reason([("Auto-Submitted", "no"), ("X-Mailer", "Apple Mail")], "Re: Bel Esprit: barrier", "ann@x.test"));
    }

    [Fact]
    public void The_providers_spam_verdict_is_respected()
    {
        Assert.Contains("6.1", AutomaticMail.SpamVerdict([("X-Spam-Status", "Yes"), ("X-Spam-Score", "6.1")]));
        Assert.Null(AutomaticMail.SpamVerdict([("X-Spam-Status", "No"), ("X-Spam-Score", "-0.1")]));
        Assert.Null(AutomaticMail.SpamVerdict([]));
    }

    [Fact]
    public void Executables_and_signature_logos_are_dropped_documents_kept()
    {
        Assert.NotNull(InboundAttachmentRules.DropReason("invoice.exe", "application/octet-stream", 50_000, null));
        Assert.NotNull(InboundAttachmentRules.DropReason("logo.png", "image/png", 4_000, "logo@01"));
        Assert.Null(InboundAttachmentRules.DropReason("photo.jpg", "image/jpeg", 2_000_000, "photo@01"));
        Assert.Null(InboundAttachmentRules.DropReason("acceptances.pdf", "application/pdf", 80_000, null));
    }

    [Fact]
    public void A_race_start_expects_mail_from_four_weeks_before_to_three_weeks_after()
    {
        var horse = new Horse(Guid.NewGuid(), "Bel Esprit", HorseNameKind.Registered, DateTimeOffset.UtcNow.AddYears(-1));
        var raceDay = new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.FromHours(11));
        var race = new Event(horse, "RaceStart", "Caulfield", raceDay);
        var vet = new Event(horse, "Veterinary", "Scope", raceDay);

        Assert.True(race.IsInWindow(raceDay.AddDays(-28)));
        Assert.True(race.IsInWindow(raceDay.AddDays(21)));
        Assert.False(race.IsInWindow(raceDay.AddDays(-30)));
        Assert.False(race.IsInWindow(raceDay.AddDays(23)));
        Assert.True(vet.IsInWindow(raceDay.AddMonths(4))); // open-ended
    }

    [Fact]
    public void A_horse_address_needs_a_well_formed_slug()
    {
        var tenant = new Tenant("Laurel Oak", "laureloak");
        var horse = new Horse(tenant.Id, "Bel Esprit", HorseNameKind.Registered, DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(() => RoutingAddress.ForHorse(horse, "Bel Esprit", DateTimeOffset.UtcNow));
        var inbox = RoutingAddress.ForHorse(horse, "bel-esprit", DateTimeOffset.UtcNow);
        Assert.Equal("bel-esprit@laureloak.in.paddockside.com.au", inbox.EmailAddress(tenant, Inbound));
    }
}
