using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Inbound;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Mailouts: an email to lists, kinds of client and people picked by hand, with an unsubscribe link for everyone, on
/// the broadcast stream, and replies kept under the mailout. Shares the sign-in tests' seed: Bel Esprit, owned by Ann
/// and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record Reason(string Why, int Count);

    private sealed record MailoutPreview(int Chosen, int Sending, List<Reason> LeftOut);

    private sealed record MailoutCreated(Guid Id, int Sending);

    private sealed record RecipientLine(Guid PartyId, string Name, string Status, string? Note);

    private sealed record ReplyLine(Guid PartyId, string Name, string Body);

    private sealed record MailoutPage(string Subject, string Audience, string Summary, List<RecipientLine> Recipients, List<ReplyLine> Replies);

    [Fact]
    public async Task A_mailout_reaches_who_was_chosen_can_be_unsubscribed_from_and_keeps_its_replies()
    {
        var seed = await SeedAsync();
        var patId = await SeedPastOwnerAsync(seed);
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        async Task<Guid> Client(string name, string? email, string? mobile = null) =>
            (await (await staff.PostApiAsync("/api/clients", new { name, email, mobile, howWeKnowThem = "Asked about shares" })).Content.ReadFromJsonAsync<ClientCreated>())!.Id;
        var rob = await Client("Rob Prospect", $"rob-{Guid.NewGuid():N}@prospects.test");
        var una = await Client("Una Gone", $"una-{Guid.NewGuid():N}@prospects.test");
        var mo = await Client("Mo Mobile", null, "0412 000 111");
        await using (var context = db.ContextFor(seed.TenantId))
        {
            (await context.Parties.SingleAsync(p => p.Id == una)).Unsubscribe(DateTimeOffset.UtcNow.AddDays(-3));
            await context.SaveChangesAsync();
        }

        var list = (await (await staff.PostApiAsync("/api/client-lists", new { name = "2026 prospective" })).Content.ReadFromJsonAsync<ListCreated>())!.Id;
        await staff.PostApiAsync($"/api/client-lists/{list}/add", new { clientIds = new[] { seed.AnnId, rob, una, mo } });
        var audience = new { listIds = new[] { list }, statuses = new[] { "Past owner" }, clientIds = Array.Empty<Guid>() };

        // Before sending: five chosen, three will get it; the unsubscribed and the one with no email are left out, and why.
        var preview = (await (await staff.PostApiAsync("/api/mailouts/preview", audience)).Content.ReadFromJsonAsync<MailoutPreview>())!;
        Assert.Equal((5, 3), (preview.Chosen, preview.Sending));
        Assert.Equal(["No email address", "Unsubscribed"], preview.LeftOut.Select(r => r.Why).Order());

        // Nothing goes until the broadcast stream exists: promotion never rides with owner updates.
        var mailout = new { subject = "Season news", body = "We've bought a colt at Easter.\n\nShares are available.", audience.listIds, audience.statuses, audience.clientIds, horseId = (Guid?)null };
        Api.Emails.CanBroadcast = false;
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync("/api/mailouts", mailout)).StatusCode);
        }
        finally
        {
            Api.Emails.CanBroadcast = true;
        }

        var sent = await staff.PostApiAsync("/api/mailouts", mailout);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var created = (await sent.Content.ReadFromJsonAsync<MailoutCreated>())!;
        Assert.Equal(3, created.Sending);
        await DispatchAsync();

        // Each on the broadcast stream, with their own reply address and unsubscribe link, in the footer and the header.
        var robEmail = Api.Emails.Sent.Single(e => e.Email.Subject == "Season news" && e.Email.ToName == "Rob Prospect");
        Assert.True(robEmail.Email.Broadcast);
        Assert.StartsWith("m-", robEmail.Email.ReplyTo);
        var unsubscribe = robEmail.Email.Headers!["List-Unsubscribe"].Trim('<', '>');
        Assert.Contains("/u/", unsubscribe);
        Assert.Contains($"Unsubscribe from these emails: {unsubscribe}", robEmail.Email.TextBody);
        Assert.Equal("List-Unsubscribe=One-Click", robEmail.Email.Headers["List-Unsubscribe-Post"]);
        Assert.Equal(3, Api.Emails.Sent.Count(e => e.Email.Subject == "Season news"));

        // Delivered and opened, as Postmark reports it.
        Assert.Equal(HttpStatusCode.OK, (await PostmarkEventAsync(new { RecordType = "Delivery", MessageID = robEmail.MessageId, DeliveredAt = "2026-10-15T08:00:00Z",
            Metadata = robEmail.Email.Metadata })).StatusCode);

        // Rob replies: it's kept under the mailout, from him.
        await InboundEmailAsync(robEmail.Email.ReplyTo, robEmail.Email.ToAddress, "Re: Season news", "Count me in for 5% of the colt.");
        var page = (await staff.GetFromJsonAsync<MailoutPage>($"/api/mailouts/{created.Id}"))!;
        var reply = Assert.Single(page.Replies);
        Assert.Equal((rob, "Count me in for 5% of the colt."), (reply.PartyId, reply.Body));
        Assert.Equal("Delivered", page.Recipients.Single(r => r.PartyId == rob).Status);
        Assert.StartsWith("Unsubscribed", page.Recipients.Single(r => r.PartyId == una).Note);
        Assert.Equal(("Left out", "No email address"), (page.Recipients.Single(r => r.PartyId == mo).Status, page.Recipients.Single(r => r.PartyId == mo).Note));
        Assert.NotEqual("Left out", page.Recipients.Single(r => r.PartyId == patId).Status); // the past owner, by kind
        Assert.Contains("2 left out", page.Summary);
        Assert.Equal("2026 prospective · Past owners", page.Audience);

        // Rob unsubscribes: the page asks first, the button (or a mail app's one-click) does it, and he's left out next time.
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var path = new Uri(unsubscribe).AbsolutePath;
        Assert.Contains("<form method=\"post\">", await anyone.GetStringAsync(path));
        await using (var check = db.ContextFor(seed.TenantId))
            Assert.Null((await check.Parties.SingleAsync(p => p.Id == rob)).UnsubscribedAt); // opening the link changes nothing
        Assert.Contains("Done.", await (await anyone.PostAsync(path, null)).Content.ReadAsStringAsync());
        preview = (await (await staff.PostApiAsync("/api/mailouts/preview", new { listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(), clientIds = new[] { rob } }))
            .Content.ReadFromJsonAsync<MailoutPreview>())!;
        Assert.Equal((1, 0), (preview.Chosen, preview.Sending));

        // A link that isn't one of ours changes nothing and says so.
        Assert.Contains("isn't one we know", await anyone.GetStringAsync("/u/nosuchbusiness/abcdefghjkmn"));

        // A viewer can't send; another tenant's mailout isn't found.
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync("/api/mailouts", mailout)).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"/api/mailouts/{created.Id}")).StatusCode);
    }

    private async Task<HttpResponseMessage> PostmarkEventAsync(object payload)
    {
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/postmark") { Content = JsonContent.Create(payload, options: new JsonSerializerOptions()) }; // Postmark's own PascalCase
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiFactory.WebhookUsername}:{ApiFactory.WebhookPassword}")));
        return await client.SendAsync(request);
    }

    /// <summary>An email arriving through Postmark's inbound webhook, then processed as the background service would.</summary>
    private async Task InboundEmailAsync(string to, string from, string subject, string text)
    {
        var messageId = Guid.NewGuid().ToString();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            From = from, FromName = "Sender", FromFull = new { Email = from, Name = "Sender", MailboxHash = "" },
            To = to, ToFull = new[] { new { Email = to, Name = "", MailboxHash = "" } }, OriginalRecipient = to,
            Subject = subject, MessageID = messageId, Date = "Thu, 15 Oct 2026 19:30:00 +1100",
            TextBody = text, HtmlBody = $"<p>{text}</p>", StrippedTextReply = "",
            Headers = new[] { new { Name = "Message-ID", Value = $"<{messageId}@mail.test>" } }, Attachments = Array.Empty<object>(),
        });
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/postmark/inbound") { Content = new ByteArrayContent(json) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiFactory.WebhookUsername}:{ApiFactory.WebhookPassword}")));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        var queue = Api.Services.GetRequiredService<IInboundQueue>();
        while ((await queue.ReceiveAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None)) is { Count: > 0 } batch)
            foreach (var message in batch)
            {
                await using var scope = Api.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<InboundProcessor>().ProcessAsync(message.TenantId, message.InboundMessageId, CancellationToken.None);
                await queue.CompleteAsync(message, CancellationToken.None);
            }
    }
}
