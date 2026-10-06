using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace Paddockside.Pipeline.Tests;

/// <summary>
/// The product test: the whole email loop against the deployed dev environment, with a real mailbox at the far end
/// (messaging-channels.md §2–§3). Staff send to the owners of a horse; the email arrives in a real inbox; a person
/// replies to it; the reply comes back through Postmark, is matched by its token and appears threaded under the
/// original on the event. If any link in the chain breaks — sending, DNS, Postmark, either webhook, the queue, the
/// matcher — this fails.
/// <para>
/// It touches real systems, so it is tagged <c>Live</c> and CI leaves it out. Run it by hand; infra/README.md
/// explains how.
/// </para>
/// </summary>
public sealed class EmailLoopTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ArriveTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(2);

    private sealed record Posted(Guid Id, int Recipients);

    private sealed record ReplyView(string Author, string Channel, string At, string Body);

    private sealed record RecipientView(string Name, string Channel, string Status, string? Note);

    private sealed record ItemView(Guid Id, string? Body, List<ReplyView> Replies, List<RecipientView> Recipients);

    private sealed record EventPage(List<ItemView> Items);

    [Fact]
    [Trait("Category", "Live")]
    public async Task A_reply_to_an_owner_email_comes_back_threaded_under_the_message()
    {
        var settings = LoopTestSettings.Load();
        var tenant = await LoopTenantSetup.EnsureAsync(settings);
        var run = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        output.WriteLine($"Run {run}: tenant {tenant.TenantId}, event {tenant.EventId}, mailbox {settings.Mailbox}");

        using var staff = await SignInAsync(settings, tenant);

        // 1. Staff write to the owners from the event page.
        var posted = await staff.PostAsync($"api/events/{tenant.EventId}/items", JsonContent.Create(new
        {
            audience = "Owners",
            scope = "Owners",
            step = (string?)null,
            channel = "Email",
            namedParties = Array.Empty<Guid>(),
            body = $"Loop test {run}: a quick check that replies reach us. Please reply to this email.",
        }));
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var messageId = (await posted.Content.ReadFromJsonAsync<Posted>())!.Id;

        // 2. The background sender hands it to Postmark.
        await Until(SendTimeout, "the email to be sent", async () =>
        {
            var recipients = (await ItemAsync(staff, tenant.EventId, messageId)).Recipients;
            var mine = Assert.Single(recipients);
            if (mine.Status is "Bounced" or "Suppressed" || (mine.Status == "Queued" && mine.Note is not null))
                Assert.Fail($"Sending failed: {mine.Status} — {mine.Note}");
            return mine.Status != "Queued";
        });

        // 3. It arrives in the real mailbox, and a person replies to it.
        var mailbox = new TestMailbox(settings);
        var email = await mailbox.WaitForAsync($"Loop test {run}", ArriveTimeout);
        var replyTo = email.ReplyTo.Mailboxes.Single().Address;
        Assert.Matches($"^r-[23456789abcdefghjkmnpqrstuvwxyz]{{12}}@{LoopTenantSetup.Slug}\\.in\\.paddockside\\.com\\.au$", replyTo);
        output.WriteLine($"Arrived; replying to {replyTo}");
        var replyText = $"Loop reply {run}: got it, thanks.";
        await mailbox.ReplyAsync(email, replyText);

        // 4. Within two minutes the reply is on the event, threaded under the message, without the quoted original.
        ReplyView? reply = null;
        await Until(ReplyTimeout, "the reply to appear under the message", async () =>
        {
            reply = (await ItemAsync(staff, tenant.EventId, messageId)).Replies.SingleOrDefault(r => r.Body.Contains(replyText));
            return reply is not null;
        });
        Assert.Equal("Loop Owner", reply!.Author);
        Assert.DoesNotContain("wrote:", reply.Body);
        Assert.DoesNotContain($"Loop test {run}", reply.Body);
    }

    private static async Task<HttpClient> SignInAsync(LoopTestSettings settings, LoopTenant tenant)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(settings.BaseUrl + "/") };
        client.DefaultRequestHeaders.Add("X-Paddockside-Request", "1");

        var password = await client.PostAsJsonAsync("api/auth/password", new { email = tenant.StaffEmail, password = tenant.StaffPassword });
        Assert.True(password.IsSuccessStatusCode, $"Password sign-in failed: {(int)password.StatusCode} {await password.Content.ReadAsStringAsync()}");
        var code = await client.PostAsJsonAsync("api/auth/totp", new { code = Totp.Code(tenant.AuthenticatorKey) });
        Assert.True(code.IsSuccessStatusCode, $"Authenticator sign-in failed: {(int)code.StatusCode} {await code.Content.ReadAsStringAsync()}");
        return client;
    }

    private static async Task<ItemView> ItemAsync(HttpClient staff, Guid eventId, Guid itemId)
    {
        var page = await staff.GetFromJsonAsync<EventPage>($"api/events/{eventId}");
        return page!.Items.Single(i => i.Id == itemId);
    }

    private async Task Until(TimeSpan timeout, string what, Func<Task<bool>> done)
    {
        var started = DateTimeOffset.UtcNow;
        while (!await done())
        {
            if (DateTimeOffset.UtcNow - started > timeout) Assert.Fail($"Waited {timeout.TotalSeconds:0} seconds for {what}.");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        output.WriteLine($"{what}: {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s");
    }
}
