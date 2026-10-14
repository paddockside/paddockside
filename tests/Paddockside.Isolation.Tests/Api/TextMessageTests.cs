using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Api.Webhooks;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Sms;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Texts out and in (messaging-channels.md §4): a short text with a sign-in link per owner, Twilio's delivery
/// reports, replies threaded under the message they answer, STOP and START, and webhooks refused unless Twilio
/// signed them. Shares the sign-in tests' seed: Bel Esprit, owned by Ann (who has a mobile) and Bob (who has not).
/// </summary>
public sealed partial class OwnerSignInTests
{
    [Fact]
    public async Task An_owner_who_wants_results_by_text_gets_one_and_can_reply_to_it()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var posted = await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = "Result", channel = "Preferred", namedParties = (Guid[]?)null, body = "Second, beaten a nose. Rode her well." })).Content.ReadFromJsonAsync<Sent>();
        Assert.Equal(3, posted!.Recipients); // Ann by email and text (results default to both), Bob by email

        await DispatchTextsAsync();
        var text = Api.Texts.Sent.Last(t => t.To == Mobile(seed));
        Assert.StartsWith($"{seed.TenantName}: Bel Esprit.", text.Body);
        Assert.Contains("Second, beaten a nose.", text.Body);
        Assert.Matches(@"Open: https://portal\.test/my/link#[A-Za-z0-9_-]{40,} or reply to this text\.$", text.Body);
        Assert.Equal($"{ApiFactory.PortalBaseUrl}/api/webhooks/twilio/status", text.StatusCallback);

        // Twilio reports it delivered.
        Assert.Equal(HttpStatusCode.OK, (await TwilioAsync(TwilioWebhooks.StatusPath, new() { ["MessageSid"] = text.Id, ["MessageStatus"] = "delivered" })).StatusCode);
        Assert.Contains("\"Delivered\"", await staff.GetStringAsync($"/api/events/{seed.EventId}"));

        // Ann texts back: it threads under the result, from her, by SMS.
        var reply = await TwilioAsync(TwilioWebhooks.IncomingPath, new() { ["From"] = Mobile(seed), ["To"] = "+61400000000", ["Body"] = "Great run! Is she ok?", ["MessageSid"] = $"SM{Guid.NewGuid():N}" });
        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response/>", await reply.Content.ReadAsStringAsync());
        var page = await staff.GetStringAsync($"/api/events/{seed.EventId}");
        Assert.Contains("Great run! Is she ok?", page);
        Assert.Contains("\"SMS\"", page);

        // The link in the text signs her in by her mobile and opens the result.
        using var phone = Api.Browser();
        var token = Regex.Match(text.Body, "#(?<t>[A-Za-z0-9_-]{40,})").Groups["t"].Value;
        var redeemed = await phone.PostApiAsync("/api/client-auth/link", new { token });
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal(Mobile(seed), (await phone.GetFromJsonAsync<Session>("/api/my/session"))!.Mobile);
    }

    [Fact]
    public async Task Stop_turns_optional_texts_off_and_start_turns_them_back_on()
    {
        var seed = await SeedAsync();
        var stop = await TwilioAsync(TwilioWebhooks.IncomingPath, new() { ["From"] = Mobile(seed), ["To"] = "+61400000000", ["Body"] = "stop", ["MessageSid"] = $"SM{Guid.NewGuid():N}" });
        Assert.Contains("Reply START to undo.", await stop.Content.ReadAsStringAsync());

        using var ann = await SignInAsync(seed.AnnEmail);
        var choices = (await ann.GetFromJsonAsync<Choices>("/api/my/notifications"))!.Categories;
        Assert.All(choices.Where(c => !c.Important), c => Assert.Equal("off", c.Text));
        Assert.Equal("on", choices.Single(c => c.Code == "results").Text); // important ones still come

        await TwilioAsync(TwilioWebhooks.IncomingPath, new() { ["From"] = Mobile(seed), ["To"] = "+61400000000", ["Body"] = "START", ["MessageSid"] = $"SM{Guid.NewGuid():N}" });
        await using var context = db.ContextFor(seed.TenantId);
        Assert.False(await context.NotificationPreferences.AnyAsync(p => p.PartyId == seed.AnnId && p.Channel == DeliveryChannel.Sms));
    }

    [Fact]
    public async Task A_text_with_nothing_recent_to_answer_waits_in_the_pending_queue()
    {
        var seed = await SeedAsync();
        await TwilioAsync(TwilioWebhooks.IncomingPath, new() { ["From"] = Mobile(seed), ["To"] = "+61400000000", ["Body"] = "Any news on the mare?", ["MessageSid"] = $"SM{Guid.NewGuid():N}" });

        await using var context = db.ContextFor(seed.TenantId);
        var pending = await context.InboundMessages.SingleAsync(m => m.Provider == "Twilio" && m.TextBody == "Any news on the mare?");
        Assert.Equal((InboundState.Pending, "Sms", seed.AnnId), (pending.State, pending.Channel, pending.PartyId));
    }

    [Fact]
    public async Task Twilio_webhooks_must_be_signed()
    {
        using var anyone = Api.Browser();
        var forged = new FormUrlEncodedContent(new Dictionary<string, string> { ["From"] = "+61491570110", ["Body"] = "STOP", ["MessageSid"] = "SMforged" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.PostAsync(TwilioWebhooks.IncomingPath, forged)).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, TwilioWebhooks.StatusPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["MessageSid"] = "SMx", ["MessageStatus"] = "delivered" }),
        };
        request.Headers.Add("X-Twilio-Signature", "not-the-signature");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.SendAsync(request)).StatusCode);
    }

    [Fact]
    public void Texts_stay_within_three_segments_and_keep_the_link()
    {
        const string link = "https://app.paddockside.com.au/my/link#abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";
        var longBody = string.Join(' ', Enumerable.Repeat("She worked beautifully on the grass this morning and pulled up well.", 20));
        var text = SmsText.ForUpdate("Laurel Oak Bloodstock", "Faultless Miss", "Trackwork", longBody, link);
        Assert.True(SmsText.Segments(text) <= SmsText.MaxSegments, $"{SmsText.Segments(text)} segments");
        Assert.EndsWith($"Open: {link} or reply to this text.", text);
        Assert.Contains("...", text);

        // Curly quotes from a word processor are made plain, so the text stays in the cheaper GSM alphabet.
        var plain = SmsText.ForUpdate("Laurel Oak", "Faultless Miss", null, "She’s “ready” – we think.", link);
        Assert.Contains("She's \"ready\" - we think.", plain);
        Assert.Equal(1, SmsText.Segments("short"));
    }

    [Theory]
    [InlineData("2026-10-14T10:30:00+11:00", false)] // mid-morning
    [InlineData("2026-10-14T21:30:00+11:00", true)]  // after nine at night
    [InlineData("2026-10-14T06:59:00+11:00", true)]  // before seven
    public void Quiet_hours_are_nine_to_seven_Sydney_time(string at, bool quiet)
    {
        Assert.Equal(quiet, SmsDispatcher.IsQuiet(DateTimeOffset.Parse(at)));
        if (quiet) Assert.Equal(7, TimeZoneInfo.ConvertTime(SmsDispatcher.QuietEnds(DateTimeOffset.Parse(at)), SmsDispatcher.Sydney).Hour);
    }

    private sealed record TallyOwner(Guid PartyId, string Name, string? Answer, string? How);

    private sealed record Tally(string Question, string Summary, List<TallyOwner> Owners);

    private sealed record StaffItem(Guid Id, string Body, List<Reply> Replies, Tally? Prompt);

    private sealed record StaffPage(List<StaffItem> Items, Dictionary<string, PromptAsk> StepPrompts);

    private sealed record PromptAsk(string Kind, string Question, int? Max);

    private sealed record OwnerQuestion(string Question, List<string> Answers, string? Mine);

    private sealed record QuestionItem(Guid Id, string Body, List<Reply> Replies, OwnerQuestion? Prompt);

    private sealed record QuestionPage(List<QuestionItem> Items);

    [Fact]
    public async Task Owners_answer_a_question_with_buttons_and_staff_see_the_tally()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // The tickets step suggests its own question.
        var suggested = (await staff.GetFromJsonAsync<StaffPage>($"/api/events/{seed.EventId}"))!.StepPrompts["Owners tickets"];
        Assert.Equal(("Count", 10), (suggested.Kind, suggested.Max));

        var posted = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items", new
        {
            audience = "Owners", scope = "Owners", step = "Owners tickets", channel = "Preferred", namedParties = (Guid[]?)null,
            body = "The club has given us 8 owners tickets.", prompt = new { kind = "Count", question = "How many owners tickets would you like?", max = 6 },
        })).Content.ReadFromJsonAsync<Sent>())!;
        await DispatchAsync();
        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.Metadata.ContainsKey("deliveryId")).Email;
        Assert.Contains("How many owners tickets would you like?", email.TextBody);
        Assert.Contains("Reply with a number from 0 to 6", email.TextBody);

        // Ann taps 2, then changes her mind to 3: her answer is the latest, and both show as her replies.
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostApiAsync($"/api/my/items/{posted.Id}/answer", new { value = "7" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostApiAsync($"/api/my/items/{seed.NoteId}/answer", new { value = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.PostApiAsync($"/api/my/items/{posted.Id}/answer", new { value = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.PostApiAsync($"/api/my/items/{posted.Id}/answer", new { value = "3" })).StatusCode);
        var mine = (await ann.GetFromJsonAsync<QuestionPage>($"/api/my/events/{seed.EventId}"))!.Items.Single(i => i.Id == posted.Id);
        Assert.Equal(("3", 7), (mine.Prompt!.Mine, mine.Prompt.Answers.Count));
        Assert.Equal(["My answer: 2", "Changed my answer to 3"], mine.Replies.Select(r => r.Body));

        // Bob rang instead: staff set his answer.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/events/{seed.EventId}/items/{posted.Id}/answers", new { partyId = seed.BobId, value = "1" })).StatusCode);

        var tally = (await staff.GetFromJsonAsync<StaffPage>($"/api/events/{seed.EventId}"))!.Items.Single(i => i.Id == posted.Id).Prompt!;
        Assert.Equal("4 asked for by 2 owners · everyone has answered", tally.Summary);
        Assert.Contains(tally.Owners, o => o.Name == "Ann Owner" && o.Answer == "3" && o.How == "in the portal");
        Assert.Contains(tally.Owners, o => o.Name == "Bob Owner" && o.Answer == "1" && o.How!.StartsWith("set by", StringComparison.Ordinal));

        // A viewer can't change answers.
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/events/{seed.EventId}/items/{posted.Id}/answers", new { partyId = seed.BobId, value = "2" })).StatusCode);
    }

    [Fact]
    public async Task A_text_reply_in_words_is_counted_as_the_answer()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var posted = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items", new
        {
            audience = "Owners", scope = "Owners", step = "Result", channel = "Preferred", namedParties = (Guid[]?)null,
            body = "She ran second.", prompt = new { kind = "YesNo", question = "Will you come to the presentation?" },
        })).Content.ReadFromJsonAsync<Sent>())!;
        await DispatchTextsAsync();
        Assert.Contains("Will you come to the presentation? Reply with your answer", Api.Texts.Sent.Last(t => t.To == Mobile(seed)).Body);

        await TwilioAsync(TwilioWebhooks.IncomingPath, new() { ["From"] = Mobile(seed), ["To"] = "+61400000000", ["Body"] = "Yes, count me in!", ["MessageSid"] = $"SM{Guid.NewGuid():N}" });

        var tally = (await staff.GetFromJsonAsync<StaffPage>($"/api/events/{seed.EventId}"))!.Items.Single(i => i.Id == posted.Id).Prompt!;
        Assert.Contains(tally.Owners, o => o.Name == "Ann Owner" && o.Answer == "Yes" && o.How == "read from their reply");
        Assert.StartsWith("Yes 1 · No 0 · 1 not answered yet", tally.Summary);
    }

    private async Task DispatchTextsAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SmsDispatcher>().DispatchAsync(CancellationToken.None);
    }

    /// <summary>Calls a Twilio webhook the way Twilio does: a form, signed with the account's auth token.</summary>
    private async Task<HttpResponseMessage> TwilioAsync(string path, Dictionary<string, string> form)
    {
        using var client = Api.Browser();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Twilio-Signature", TwilioSignature.Compute(ApiFactory.TwilioAuthToken, $"{ApiFactory.PortalBaseUrl}{path}", form));
        return await client.SendAsync(request);
    }
}
