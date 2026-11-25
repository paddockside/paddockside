using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Emails that come from the business's own address, and replies to bulk emails answered where they land (David,
/// 25 Nov 2026). Shares the sign-in tests' seed: Ann and Bob, owners of Bel Esprit.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record SenderState(string? Address, string? Name, bool On, string? Tested, string? Problem, string Otherwise);

    private sealed record ConversationMessage(Guid Id, bool Out, string Who, string Body, string? Delivery, string? Note);

    private sealed record ConversationLine(Guid RecipientId, Guid PartyId, string Name, string Address, bool Waiting, List<ConversationMessage> Messages);

    private sealed record WithConversations(List<ConversationLine> Conversations);

    private sealed record ToAnswerLine(Guid Id, bool Social, string Title, string Detail);

    private sealed record TodayToAnswer(List<ToAnswerLine> ToAnswer);

    [Fact]
    public async Task Emails_come_from_the_business_address_only_once_tested_and_turned_on_and_never_wait_if_it_is_refused()
    {
        var seed = await SeedAsync();
        var (ops, _) = await Api.SignedInOperatorAsync();
        var path = $"/api/ops/tenants/{seed.TenantId}/email/sender";
        var office = $"office@own-{Guid.NewGuid():N}.com.au";
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        async Task<OutboundEmail> Mail(string subject)
        {
            Assert.Equal(HttpStatusCode.Created, (await staff.PostApiAsync("/api/mailouts", new { subject, body = "News.", listIds = Array.Empty<Guid>(),
                statuses = Array.Empty<string>(), clientIds = new[] { seed.AnnId }, horseId = (Guid?)null })).StatusCode);
            await DispatchAsync();
            return Api.Emails.Sent.Single(e => e.Email.Subject == subject && e.Email.ToAddress == seed.AnnEmail).Email;
        }

        // Not theirs to send as, not an address, or ours: refused. A real one is saved, off.
        foreach (var bad in new[] { "someone@gmail.com", "not an address", "m-abc@x.in.paddockside.com.au" })
            Assert.Equal(HttpStatusCode.BadRequest, (await ops.PutApiAsync(path, new { address = bad, name = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PutApiAsync(path, new { address = office, name = "Bad <name>" })).StatusCode);
        var state = (await (await ops.PutApiAsync(path, new { address = office.ToUpperInvariant(), name = "Laurel Oak" })).Content.ReadFromJsonAsync<SenderState>())!;
        Assert.Equal((office, "Laurel Oak", false), (state.Address, state.Name, state.On));

        // Not turned on until a test is accepted and checked; meanwhile everything goes as before.
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostApiAsync($"{path}/on", new { @checked = true })).StatusCode);
        Assert.Equal("updates@mail.paddockside.com.au", (await Mail("Before")).FromAddress);
        Api.Emails.RefusedSenders.Add(office);
        var refused = await ops.PostApiAsync($"{path}/test", new { email = "operator@test.example.org" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("add the domain", await refused.Content.ReadAsStringAsync());
        Api.Emails.RefusedSenders.Remove(office);
        Assert.Equal(HttpStatusCode.OK, (await ops.PostApiAsync($"{path}/test", new { email = "operator@test.example.org" })).StatusCode);
        Assert.Contains(Api.Emails.Sent, e => e.Email.FromAddress == office && e.Email.ToAddress == "operator@test.example.org" && e.Email.ReplyToName == "Laurel Oak");
        Assert.Equal("updates@mail.paddockside.com.au", (await Mail("Tested but off")).FromAddress);
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostApiAsync($"{path}/on", new { @checked = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.PostApiAsync($"{path}/on", new { @checked = true })).StatusCode);

        // On: the business alone, with no "via Paddockside"; replies still come to the recipient's own address, under its name.
        var on = await Mail("On");
        Assert.Equal((office, "Laurel Oak", "Laurel Oak"), (on.FromAddress, on.FromName, on.ReplyToName));
        Assert.StartsWith("m-", on.ReplyTo);
        Assert.True(on.Broadcast);

        // Signing in too, and a reply to that goes to the office.
        using (var browser = Api.Browser()) await browser.PostApiAsync("/api/client-auth/email", new { email = seed.AnnEmail });
        var signIn = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.Metadata.GetValueOrDefault("purpose") == "sign-in").Email;
        Assert.Equal((office, "Laurel Oak", office), (signIn.FromAddress, signIn.FromName, signIn.ReplyTo));

        // And a horse update to its owners.
        var horseSubject = $"Update {Guid.NewGuid():N}";
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var tenant = await context.Tenants.SingleAsync();
            var horse = await context.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).SingleAsync(h => h.Id == seed.HorseId);
            var item = StreamItem.Message(horse, null, horseSubject, "She worked well.", StreamItemScope.Owners, DateTimeOffset.UtcNow, "Staff");
            context.Add(item);
            await OutboundQueue.QueueAsync(context, tenant, horse, null, item, DeliveryChannel.Email, false, DateTimeOffset.UtcNow, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await DispatchAsync();
        var updates = Api.Emails.Sent.Where(e => e.Email.Subject.Contains(horseSubject)).Select(e => e.Email).ToList();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Equal((office, "Laurel Oak"), (u.FromAddress, u.FromName)));
        Assert.All(updates, u => Assert.StartsWith("r-", u.ReplyTo));

        // Refused one day (the domain's records have gone): the email still goes, from ours, and it's off until it's put right.
        Api.Emails.RefusedSenders.Add(office);
        var fallback = await Mail("Refused");
        Assert.Equal("updates@mail.paddockside.com.au", fallback.FromAddress);
        Assert.Null(fallback.ReplyToName);
        state = (await ops.GetFromJsonAsync<SenderState>(path))!;
        Assert.Equal((false, (string?)null), (state.On, state.Tested));
        Assert.Contains("refused", state.Problem);
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostApiAsync($"{path}/on", new { @checked = true })).StatusCode); // a fresh test first
        Assert.Contains("was refused", await staff.GetStringAsync("/api/today"));
        Api.Emails.RefusedSenders.Remove(office);

        // Put right, turned on, turned off; a different address starts again. All in the business's audit log.
        await ops.PostApiAsync($"{path}/test", new { email = "operator@test.example.org" });
        await ops.PostApiAsync($"{path}/on", new { @checked = true });
        Assert.False((await (await ops.PostApiAsync($"{path}/off", new { })).Content.ReadFromJsonAsync<SenderState>())!.On);
        await ops.PostApiAsync($"{path}/on", new { @checked = true });
        state = (await (await ops.PutApiAsync(path, new { address = $"info@{office.Split('@')[1]}", name = "Laurel Oak" })).Content.ReadFromJsonAsync<SenderState>())!;
        Assert.Equal((false, (string?)null), (state.On, state.Tested));
        await using (var context = db.ContextFor(seed.TenantId))
            Assert.True(await context.AuditEntries.CountAsync(a => a.Action == "email.sender") >= 5);

        // Operators only.
        using var admin = await Api.SignedInAsync(seed.TenantId, MemberRole.TenantAdmin);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.PostApiAsync($"{path}/on", new { @checked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ops.GetAsync($"/api/ops/tenants/{Guid.NewGuid()}/email/sender")).StatusCode);
    }

    [Fact]
    public async Task A_reply_to_an_invitation_is_on_the_event_and_Today_and_answered_from_there()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = (await (await staff.PostApiAsync("/api/social", new { title = "Cup Lunch", kind = "Lunch", date = day })).Content.ReadFromJsonAsync<SocialMade>())!.Id;
        await staff.PostApiAsync($"/api/social/{id}/invite", new { subject = "Cup Lunch", body = "Join us.", listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(), clientIds = new[] { seed.AnnId, seed.BobId } });
        await DispatchAsync();
        var invitation = Api.Emails.Sent.Single(e => e.Email.Subject == "Cup Lunch" && e.Email.ToAddress == seed.AnnEmail).Email;
        Assert.Empty((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);

        // Ann writes back, from another address of hers: on the event, waiting, and on Today.
        const string other = "ann.elsewhere@example.test";
        await InboundEmailAsync(invitation.ReplyTo, other, "RE: Cup Lunch", "Is there parking?");
        var conversation = Assert.Single((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);
        Assert.Equal((seed.AnnId, other, true), (conversation.PartyId, conversation.Address, conversation.Waiting));
        Assert.Equal("Is there parking?", Assert.Single(conversation.Messages).Body);
        var todo = Assert.Single((await staff.GetFromJsonAsync<TodayToAnswer>("/api/today"))!.ToAnswer);
        Assert.Equal((id, true, "Cup Lunch"), (todo.Id, todo.Social, todo.Title));
        Assert.StartsWith("1 reply; the latest from Ann Owner", todo.Detail);

        // A viewer reads; another business sees nothing; an empty answer isn't one.
        var answer = $"/api/mailouts/conversations/{conversation.RecipientId}/answer";
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync(answer, new { body = "Yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/mailouts/conversations/{conversation.RecipientId}/done", new { })).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.PostApiAsync(answer, new { body = "Yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(answer, new { body = " " })).StatusCode);

        // Answered: to where she wrote from, alone, threaded, not a mailout; and off Today. Unsubscribing doesn't stop it.
        await using (var context = db.ContextFor(seed.TenantId))
        {
            (await context.Parties.SingleAsync(p => p.Id == seed.AnnId)).Unsubscribe(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await staff.PostApiAsync(answer, new { body = "Yes, under the building." })).StatusCode);
        Assert.Empty((await staff.GetFromJsonAsync<TodayToAnswer>("/api/today"))!.ToAnswer);
        await DispatchAsync();
        var sent = Api.Emails.Sent.Single(e => e.Email.ToAddress == other);
        Assert.Equal(("RE: Cup Lunch", invitation.ReplyTo, false), (sent.Email.Subject, sent.Email.ReplyTo, sent.Email.Broadcast));
        Assert.Contains("Yes, under the building.", sent.Email.TextBody);
        Assert.Matches("^<.+@mail.test>$", sent.Email.Headers!["In-Reply-To"]);
        Assert.DoesNotContain("List-Unsubscribe", sent.Email.Headers.Keys);

        // Delivered, as Postmark reports it; a report for another message changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await PostmarkEventAsync(new { RecordType = "Bounce", Type = "HardBounce", MessageID = Guid.NewGuid().ToString(), Metadata = sent.Email.Metadata })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostmarkEventAsync(new { RecordType = "Delivery", MessageID = sent.MessageId, DeliveredAt = "2026-11-25T08:00:00Z", Metadata = sent.Email.Metadata })).StatusCode);
        conversation = Assert.Single((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);
        Assert.False(conversation.Waiting);
        Assert.Equal([(false, (string?)null), (true, "Delivered")], conversation.Messages.Select(m => (m.Out, m.Delivery)));

        // She answers the answer: the same conversation, waiting again; "no answer needed" sets it aside and sends nothing.
        await InboundEmailAsync(sent.Email.ReplyTo, other, "RE: Cup Lunch", "Perfect, thanks.");
        conversation = Assert.Single((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);
        Assert.Equal((true, 3), (conversation.Waiting, conversation.Messages.Count));
        var count = Api.Emails.Sent.Count;
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/mailouts/conversations/{conversation.RecipientId}/done", new { })).StatusCode);
        await DispatchAsync();
        Assert.Equal(count, Api.Emails.Sent.Count);
        conversation = Assert.Single((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);
        Assert.False(conversation.Waiting);
        Assert.StartsWith("No answer needed", conversation.Messages[^1].Note);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostApiAsync($"/api/mailouts/conversations/{Guid.NewGuid()}/answer", new { body = "Hi" })).StatusCode);

        // An address that refuses it for good says so, and isn't tried again.
        await InboundEmailAsync(sent.Email.ReplyTo, "gone@example.test", "Re: Cup Lunch", "One more thing");
        Api.Emails.Inactive.Add("gone@example.test");
        await staff.PostApiAsync(answer, new { body = "Go on" });
        await DispatchAsync();
        conversation = Assert.Single((await staff.GetFromJsonAsync<WithConversations>($"/api/social/{id}"))!.Conversations);
        Assert.Equal("Not sent", conversation.Messages[^1].Delivery);
        Assert.Contains("inactive", conversation.Messages[^1].Note);
    }

    /// <summary>What Postmark is asked to send, and what its answers mean.</summary>
    private sealed class PostmarkStub(HttpStatusCode status, object answer) : HttpMessageHandler
    {
        public JsonElement Asked { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
            return new HttpResponseMessage(status) { Content = JsonContent.Create(answer, options: new JsonSerializerOptions()) };
        }
    }

    [Theory]
    [InlineData(400, true, false)] // the From address's domain isn't in the account
    [InlineData(401, true, false)] // it's there, not confirmed
    [InlineData(406, false, true)] // the recipient's address is inactive
    [InlineData(405, false, false)] // not allowed to send just now: try again
    public async Task Postmark_refusing_the_sender_is_never_taken_for_a_bad_recipient(int code, bool senderRefused, bool permanent)
    {
        var stub = new PostmarkStub(HttpStatusCode.UnprocessableEntity, new { ErrorCode = code, Message = "No" });
        var postmark = new PostmarkEmailSender(new HttpClient(stub) { BaseAddress = new Uri("https://postmark.test/") },
            Options.Create(new PostmarkOptions { ServerToken = "test" }), NullLogger<PostmarkEmailSender>.Instance);
        var email = new OutboundEmail("office@laurel.com.au", "Laurel Oak", "ann@owners.com.au", "Ann", "m-abc@laurel.in.paddockside.com.au", "Hi", "<p>Hi</p>", "Hi",
            new Dictionary<string, string>(), ReplyToName: "Laurel Oak, Office");
        var result = await postmark.SendAsync(email, CancellationToken.None);
        Assert.Equal((false, senderRefused, permanent), (result.Accepted, result.SenderRefused, result.PermanentFailure));
        Assert.Equal("\"Laurel Oak, Office\" <m-abc@laurel.in.paddockside.com.au>", stub.Asked.GetProperty("replyTo").GetString());
        await postmark.SendAsync(email with { ReplyToName = null }, CancellationToken.None);
        Assert.Equal("m-abc@laurel.in.paddockside.com.au", stub.Asked.GetProperty("replyTo").GetString()); // as before, unless it's set
    }
}
