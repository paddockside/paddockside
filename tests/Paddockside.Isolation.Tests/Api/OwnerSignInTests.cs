using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Passwordless owner sign-in (identity-access.md §3, §4.1, §6): email links and codes, SMS codes, notification
/// links, invitations, linking the party, long sessions on a known device — and that a signed-in owner sees only
/// what the access rule gives them.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed partial class OwnerSignInTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record Signed(string Destination);

    private sealed record Session(string Name, string? Email, string? Mobile, string TenantName);

    private sealed record Horse(Guid Id, string Name);

    private sealed record Reply(string Author, string Body);

    private sealed record Item(Guid Id, string Kind, string? Title, string Body, List<Reply> Replies, bool CanReply);

    private sealed record EventPage(Guid HorseId, string Horse, List<Item> Items);

    private sealed record Seed(Guid TenantId, string TenantName, Guid HorseId, Guid EventId, Guid AnnId, string AnnEmail, string AnnMobile, Guid BobId, Guid MessageId, Guid NoteId, Guid TrainerMessageId);

    [GeneratedRegex(@"/my/link#(?<token>[A-Za-z0-9_-]{40,})")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"code (?<code>\d{6})")]
    private static partial Regex CodePattern();

    [Fact]
    public async Task An_emailed_link_signs_an_owner_in_once_and_links_their_party()
    {
        var seed = await SeedAsync();
        using var laptop = Api.Browser();
        Assert.Equal(HttpStatusCode.Accepted, (await laptop.PostApiAsync("/api/client-auth/email", new { email = seed.AnnEmail })).StatusCode);

        // Opened on the phone, not the laptop that asked: the link works anywhere, once.
        var token = LatestLinkTo(seed.AnnEmail);
        using var phone = Api.Browser();
        var signed = await phone.PostApiAsync("/api/client-auth/link", new { token });
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal("/my/horses", (await signed.Content.ReadFromJsonAsync<Signed>())!.Destination);

        var session = await phone.GetFromJsonAsync<Session>("/api/my/session");
        Assert.Equal("Ann Owner", session!.Name);
        Assert.Equal(seed.TenantName, session.TenantName);
        Assert.Equal(["Bel Esprit"], (await phone.GetFromJsonAsync<List<Horse>>("/api/my/horses"))!.Select(h => h.Name));

        using var again = Api.Browser();
        var reused = await again.PostApiAsync("/api/client-auth/link", new { token });
        Assert.Equal(HttpStatusCode.Gone, reused.StatusCode);
        Assert.Contains("already been used", await reused.Content.ReadAsStringAsync());

        // The party is now this person, with an Owner membership in the tenant.
        await using var tenant = db.ContextFor(seed.TenantId);
        var personId = (await tenant.Parties.SingleAsync(p => p.Id == seed.AnnId)).PersonId;
        Assert.NotNull(personId);
        await using var scope = Api.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
        Assert.True(await identity.Memberships.AnyAsync(m => m.PersonId == personId && m.TenantId == seed.TenantId && m.Role == MemberRole.Owner && m.AcceptedAt != null));
    }

    [Fact]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        using var browser = Api.Browser();
        var address = $"nobody-{Guid.NewGuid():N}@nowhere.test";

        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostApiAsync("/api/client-auth/email", new { email = address })).StatusCode);
        Assert.DoesNotContain(Api.Emails.Sent, s => s.Email.ToAddress == address);
    }

    [Fact]
    public async Task The_code_works_only_in_the_browser_that_asked_and_allows_five_tries()
    {
        var seed = await SeedAsync();
        using var laptop = Api.Browser();
        await laptop.PostApiAsync("/api/client-auth/email", new { email = seed.AnnEmail });
        var code = LatestCodeTo(seed.AnnEmail);

        using var stranger = Api.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.PostApiAsync("/api/client-auth/code", new { code })).StatusCode);

        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) await laptop.PostApiAsync("/api/client-auth/code", new { code = wrong });
        var fifth = await laptop.PostApiAsync("/api/client-auth/code", new { code = wrong });
        Assert.Contains("last try", await fifth.Content.ReadAsStringAsync());

        var tooLate = await laptop.PostApiAsync("/api/client-auth/code", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, tooLate.StatusCode); // spent, even with the right code
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/my/session")).StatusCode);
    }

    [Fact]
    public async Task A_texted_code_signs_in_by_mobile_however_the_number_is_written()
    {
        var seed = await SeedAsync();
        using var phone = Api.Browser();
        Assert.Equal(HttpStatusCode.Accepted, (await phone.PostApiAsync("/api/client-auth/sms", new { mobile = "+61 4" + Mobile(seed)[4..] })).StatusCode);

        var text = Api.Texts.Sent.Last(t => t.To == Mobile(seed));
        var code = Regex.Match(text.Body, @"^\d{6}").Value;
        var signed = await phone.PostApiAsync("/api/client-auth/code", new { code = code[..3] + " " + code[3..] });

        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal(Mobile(seed), (await phone.GetFromJsonAsync<Session>("/api/my/session"))!.Mobile);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostApiAsync("/api/client-auth/sms", new { mobile = "not a number" })).StatusCode);
    }

    [Fact]
    public async Task A_notification_link_opens_the_message_signs_in_and_works_once()
    {
        var seed = await SeedAsync();
        using (var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator))
            Assert.Equal(HttpStatusCode.Created, (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
                new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = Array.Empty<Guid>(), body = "Barrier 4. Tickets at the gate." })).StatusCode);
        await DispatchAsync();

        var email = Api.Emails.Sent.Last(s => s.Email.ToAddress == seed.AnnEmail && s.Email.TextBody.Contains("Barrier 4.")).Email;
        Assert.Contains("Open in the owners' portal", email.TextBody);
        var token = LinkPattern().Match(email.TextBody).Groups["token"].Value;

        using var phone = Api.Browser();
        var destination = (await (await phone.PostApiAsync("/api/client-auth/link", new { token })).Content.ReadFromJsonAsync<Signed>())!.Destination;
        Assert.StartsWith($"/my/events/{seed.EventId}#item-", destination);
        var page = await phone.GetFromJsonAsync<EventPage>($"/api/my/events/{seed.EventId}");
        Assert.Contains(page!.Items, i => i.Body.Contains("Barrier 4.") && destination.EndsWith(i.Id.ToString()));

        // Used: lands on the plain sign-in page, but remembers where it was going.
        using var later = Api.Browser();
        var spent = await later.PostApiAsync("/api/client-auth/link", new { token });
        Assert.Equal(HttpStatusCode.Gone, spent.StatusCode);
        Assert.Equal(destination, JsonDocument.Parse(await spent.Content.ReadAsStringAsync()).RootElement.GetProperty("returnPath").GetString());
    }

    [Fact]
    public async Task An_owner_sees_only_what_is_theirs_and_none_of_the_staff_side()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);

        var page = await ann.GetFromJsonAsync<EventPage>($"/api/my/events/{seed.EventId}");
        var ids = page!.Items.Select(i => i.Id).ToList();
        Assert.Contains(seed.MessageId, ids);
        Assert.DoesNotContain(seed.NoteId, ids);            // internal
        Assert.DoesNotContain(seed.TrainerMessageId, ids);  // trainer's, never owners' (D12)

        var message = page.Items.Single(i => i.Id == seed.MessageId);
        Assert.Equal(["Ann's own reply"], message.Replies.Select(r => r.Body)); // never Bob's
        Assert.All(message.Replies, r => Assert.Equal("You", r.Author));

        // A corrected fact shows once, as corrected, in the feed too.
        var updates = await ann.GetStringAsync("/api/my/updates");
        Assert.Single(Regex.Matches(updates, "Barrier draw"));
        Assert.Contains("Barrier: 9", updates);

        Assert.Equal(HttpStatusCode.Forbidden, (await ann.GetAsync("/api/horses")).StatusCode);              // staff API
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/my/events/{db.A.EventId}")).StatusCode); // another tenant's
    }

    [Fact]
    public async Task A_reply_from_the_portal_threads_under_the_message_for_staff()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);

        var posted = await ann.PostApiAsync($"/api/my/items/{seed.MessageId}/replies", new { body = "Two tickets please." });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostApiAsync($"/api/my/items/{seed.NoteId}/replies", new { body = "Sneaky" })).StatusCode);

        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var page = await staff.GetStringAsync($"/api/events/{seed.EventId}");
        Assert.Contains("Two tickets please.", page);
        Assert.Contains("Portal", page);
    }

    [Fact]
    public async Task A_first_interest_queues_one_invitation_whose_link_signs_in()
    {
        var seed = await SeedAsync();
        await using (var tenant = db.ContextFor(seed.TenantId))
        {
            var invitations = await tenant.OwnerInvitations.ToListAsync();
            Assert.Equal(new[] { seed.AnnId, seed.BobId }.Order(), invitations.Select(i => i.PartyId).Order());
        }

        await DispatchAsync();
        var invitation = Api.Emails.Sent.Last(s => s.Email.ToAddress == seed.AnnEmail && s.Email.Metadata.ContainsKey("invitationId")).Email;
        Assert.Equal("Welcome to Bel Esprit", invitation.Subject);
        Assert.Matches("^bel-esprit@", invitation.ReplyTo); // a reply reaches staff on the horse

        using var phone = Api.Browser();
        var signed = await phone.PostApiAsync("/api/client-auth/link", new { token = LinkPattern().Match(invitation.TextBody).Groups["token"].Value });
        Assert.Equal("/my/horses", (await signed.Content.ReadFromJsonAsync<Signed>())!.Destination);

        // A second interest is not a first interest: no second invitation.
        await using (var tenant = db.ContextFor(seed.TenantId))
        {
            var horse = new Paddockside.Domain.Horse(seed.TenantId, "Second Horse", HorseNameKind.Registered, DateTimeOffset.UtcNow);
            horse.OpenManagementPeriod(DateTimeOffset.UtcNow);
            horse.AddInterest(await tenant.Parties.SingleAsync(p => p.Id == seed.AnnId), await tenant.HoldingEntities.FirstAsync(), 100, DateTimeOffset.UtcNow);
            tenant.Add(horse);
            await tenant.SaveChangesAsync();
            Assert.Equal(1, await tenant.OwnerInvitations.CountAsync(i => i.PartyId == seed.AnnId));
        }
    }

    [Fact]
    public async Task A_tenant_can_turn_invitations_off()
    {
        var seed = await SeedAsync(invite: false);
        await using var tenant = db.ContextFor(seed.TenantId);
        Assert.False(await tenant.OwnerInvitations.AnyAsync());
    }

    [Fact]
    public async Task A_session_lasts_a_day_on_a_new_device_and_ninety_on_one_used_before()
    {
        var seed = await SeedAsync();
        using var phone = Api.Browser();

        var first = await SignInResponseAsync(phone, seed.AnnEmail);
        Assert.InRange(SessionExpiry(first) - DateTimeOffset.UtcNow, TimeSpan.FromHours(23), TimeSpan.FromHours(25));

        await phone.PostApiAsync("/api/auth/sign-out", new { });
        // Back again on the same phone, by text this time (a second email within a minute is held back).
        await phone.PostApiAsync("/api/client-auth/sms", new { mobile = Mobile(seed) });
        var code = Regex.Match(Api.Texts.Sent.Last(t => t.To == Mobile(seed)).Body, @"^\d{6}").Value;
        var second = await phone.PostApiAsync("/api/client-auth/code", new { code });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var both = await phone.GetFromJsonAsync<Session>("/api/my/session"); // one person: the email sign-in and the text found the same Ann
        Assert.Equal((seed.AnnEmail, Mobile(seed)), (both!.Email, both.Mobile));
        Assert.InRange(SessionExpiry(second) - DateTimeOffset.UtcNow, TimeSpan.FromDays(89), TimeSpan.FromDays(91));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string Mobile(Seed seed) => seed.AnnMobile;

    private async Task<HttpClient> SignInAsync(string address)
    {
        var browser = Api.Browser();
        var response = await SignInResponseAsync(browser, address);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return browser;
    }

    private async Task<HttpResponseMessage> SignInResponseAsync(HttpClient browser, string address)
    {
        await browser.PostApiAsync("/api/client-auth/email", new { email = address });
        return await browser.PostApiAsync("/api/client-auth/link", new { token = LatestLinkTo(address) });
    }

    private static DateTimeOffset SessionExpiry(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-paddockside=", StringComparison.Ordinal));
        var expires = Regex.Match(cookie, "expires=(?<at>[^;]+)", RegexOptions.IgnoreCase).Groups["at"].Value;
        return DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string LatestLinkTo(string address) =>
        LinkPattern().Match(Api.Emails.Sent.Last(s => s.Email.ToAddress == address && s.Email.Metadata.GetValueOrDefault("purpose") == "sign-in").Email.TextBody).Groups["token"].Value;

    private string LatestCodeTo(string address) =>
        CodePattern().Match(Api.Emails.Sent.Last(s => s.Email.ToAddress == address && s.Email.Metadata.GetValueOrDefault("purpose") == "sign-in").Email.Subject).Groups["code"].Value;

    private async Task DispatchAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchAsync(CancellationToken.None);
    }

    /// <summary>
    /// A tenant of its own. Bel Esprit, owned by Ann and Bob. Her race has a message to owners (Bob and Ann each
    /// replied), a staff note and a message to the trainer.
    /// </summary>
    private async Task<Seed> SeedAsync(bool invite = true)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Owners {suffix}", $"ow{suffix}") { InviteOwnersOnFirstInterest = invite };
        var syndicate = new HoldingEntity(tenant.Id, "Syndicate", HoldingEntityType.ManagedSyndicate);
        var ann = new Party(tenant.Id, "Ann Owner");
        var bob = new Party(tenant.Id, "Bob Owner");
        var annEmail = $"ann-{suffix}@owners.test";
        var annMobile = $"+614{System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 100_000_000):D8}"; // her own number in every seed
        ann.AddEmail(annEmail);
        ann.AddMobile(annMobile);
        bob.AddEmail($"bob-{suffix}@owners.test");
        var horse = new Paddockside.Domain.Horse(tenant.Id, "Bel Esprit", HorseNameKind.Registered, now.AddYears(-1));
        horse.OpenManagementPeriod(now.AddYears(-1));
        horse.AddInterest(ann, syndicate, 500, now.AddYears(-1));
        horse.AddInterest(bob, syndicate, 500, now.AddYears(-1));
        var race = new Paddockside.Domain.Event(horse, "RaceStart", "Caulfield, Saturday", now.AddDays(5));
        race.Open(now.AddDays(-10));

        var message = StreamItem.Message(horse, race, "Tickets", "How many tickets would you like?", StreamItemScope.Owners, now.AddDays(-2), "Kate");
        var bobReply = message.Reply(horse, race, "Bob Owner", bob.Id, "Email", "Bob's private reply", now.AddDays(-1));
        var annReply = message.Reply(horse, race, "Ann Owner", ann.Id, "Email", "Ann's own reply", now.AddDays(-1));
        var note = StreamItem.Note(horse, race, "Bob is slow to pay.", now.AddDays(-1), "Kate");
        var trainer = StreamItem.Message(horse, race, null, "Please confirm the gear change.", StreamItemScope.Trainer, now.AddDays(-1), "Kate");
        var barrier = StreamItem.Fact(horse, race, "Barrier draw", "Racing Victoria", [new FactField("Barrier", "7")], now.AddDays(-3));
        var corrected = barrier.Correct(horse, race, [new FactField("Barrier", "9")], now.AddDays(-3).AddHours(2), "Scratching");

        await using var context = db.ContextFor(tenant.Id);
        context.AddRange(tenant, syndicate, ann, bob, horse, race, message, bobReply, annReply, note, trainer, barrier, corrected);
        await context.SaveChangesAsync();
        return new Seed(tenant.Id, tenant.Name, horse.Id, race.Id, ann.Id, annEmail, annMobile, bob.Id, message.Id, note.Id, trainer.Id);
    }
}
