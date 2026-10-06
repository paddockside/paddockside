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
/// Inbound email end to end (messaging-channels.md §3): Postmark's webhook stores the message verbatim and queues it;
/// processing places replies by token (tier 1) and horse-inbox mail by horse (tier 2), sends the rest to Pending,
/// files robots as Ignored and spam as Held — and never lets a token cross tenants.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class InboundEmailTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private const string InboundPath = "/api/webhooks/postmark/inbound";

    private ApiFactory Api => api.For(db);

    private sealed record Posted(Guid Id, int Recipients);

    private sealed record Seed(Guid TenantId, string Slug, Guid HorseId, Guid EventId, Guid AnnId, Guid TomId, string TomEmail);

    [Fact]
    public async Task The_inbound_webhook_needs_the_credentials()
    {
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsync(InboundPath, new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_reply_to_a_token_is_threaded_under_the_original_message()
    {
        var seed = await SeedTenantAsync(eventInWindow: true);
        var messageId = await PostToOwnersAsync(seed, "Barrier 9 for Saturday. Can you come?");
        var token = await TokenForAsync(seed, messageId, seed.AnnId);

        // From Ann's personal address, which we have never seen: the token still identifies her exactly.
        var payload = Payload(
            to: $"r-{token}@{seed.Slug}.in.paddockside.com.au",
            from: "ann.personal@gmail.test",
            subject: "Re: Bel Esprit",
            text: "Count me in.\n\nOn Tue, 6 Oct 2026 at 7:00 pm, Laurel Oak <updates@mail.paddockside.com.au> wrote:\n> Barrier 9 for Saturday.",
            attachments:
            [
                Attachment("acceptances.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 test")),
                Attachment("logo.png", "image/png", new byte[900], contentId: "logo@01"),
                Attachment("run.exe", "application/octet-stream", new byte[100]),
            ]);
        Assert.Equal(HttpStatusCode.OK, (await InboundAsync(payload)).StatusCode);

        // Stored first, exactly as it arrived.
        var stored = await InboundFor(seed.TenantId, payload.MessageId);
        Assert.Equal(InboundState.Received, stored.State);
        var store = Api.Services.GetRequiredService<IInboundStore>();
        Assert.Equal(payload.Bytes, await store.ReadAsync(stored.RawBlobName, CancellationToken.None));
        Assert.Contains("X-Mailer", stored.Headers);
        var pdf = stored.Attachments.Single(a => a.FileName == "acceptances.pdf");
        Assert.Equal("%PDF-1.4 test", Encoding.ASCII.GetString((await store.ReadAsync(pdf.BlobName!, CancellationToken.None))!));
        Assert.NotNull(stored.Attachments.Single(a => a.FileName == "logo.png").DroppedReason);
        Assert.NotNull(stored.Attachments.Single(a => a.FileName == "run.exe").DroppedReason);

        await ProcessQueueAsync();

        await using var check = db.ContextFor(seed.TenantId);
        var processed = await check.InboundMessages.SingleAsync(m => m.Id == stored.Id);
        Assert.Equal(InboundState.Placed, processed.State);
        Assert.Equal(1, processed.MatchTier);
        Assert.Equal(seed.AnnId, processed.PartyId);

        var reply = await check.StreamItems.SingleAsync(i => i.Id == processed.StreamItemId);
        Assert.Equal(messageId, reply.InReplyToId);
        Assert.Equal(seed.EventId, reply.EventId);
        Assert.Equal("Count me in.", reply.Body);
        Assert.Equal(seed.AnnId, reply.AuthorPartyId);
        Assert.Equal([seed.AnnId], reply.NamedPartyIds); // Ann and staff only: never the other owners
        Assert.Contains(stored.TextBody!, processed.TextBody); // the full body is kept
        Assert.Equal(DeliveryStatus.Replied, (await check.Deliveries.SingleAsync(d => d.StreamItemId == messageId && d.PartyId == seed.AnnId)).Status);

        // And the staff event page shows it in the thread, without the quoted history.
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var page = await coordinator.GetStringAsync($"/api/events/{seed.EventId}");
        Assert.Contains("Count me in.", page);
        Assert.DoesNotContain("wrote:", page);
    }

    [Fact]
    public async Task Mail_to_a_horse_address_goes_on_its_one_open_event_in_window()
    {
        var seed = await SeedTenantAsync(eventInWindow: true);
        var payload = Payload($"bel-esprit@{seed.Slug}.in.paddockside.com.au", seed.TomEmail, "Bel Esprit: worked well", "Galloped 800m this morning, very happy.");

        await InboundAsync(payload);
        await ProcessQueueAsync();

        await using var check = db.ContextFor(seed.TenantId);
        var message = await check.InboundMessages.SingleAsync(m => m.ProviderMessageId == payload.MessageId);
        Assert.Equal(InboundState.Placed, message.State);
        Assert.Equal(2, message.MatchTier);
        var item = await check.StreamItems.SingleAsync(i => i.Id == message.StreamItemId);
        Assert.Equal(seed.EventId, item.EventId);
        Assert.Equal(StreamItemScope.Internal, item.Scope); // staff decide what owners see
        Assert.Equal(seed.TomId, item.AuthorPartyId); // known sender, by exact address
        Assert.Equal("Bel Esprit: worked well", item.Title);
    }

    [Fact]
    public async Task Mail_to_a_horse_with_no_event_in_window_is_parked_on_the_horse_and_old_names_still_work()
    {
        // Tenant A's horse has a registered name and a stable name; its race was in 2024, long out of window.
        var payload = Payload("tenant-a-stable-name@tenanta.in.paddockside.com.au", "vet@clinic.test", "Teeth done", "All fine.");

        await InboundAsync(payload);
        await ProcessQueueAsync();

        await using var a = db.ContextFor(db.A.TenantId);
        var message = await a.InboundMessages.SingleAsync(m => m.ProviderMessageId == payload.MessageId);
        Assert.Equal(InboundState.Placed, message.State);
        Assert.Equal(db.A.HorseId, message.HorseId);
        Assert.Null(message.EventId);
        Assert.Contains("parked on the horse", message.Reason);
        Assert.Null((await a.StreamItems.SingleAsync(i => i.Id == message.StreamItemId)).EventId);
    }

    [Fact]
    public async Task Anything_else_goes_to_pending_where_staff_can_see_it()
    {
        var seed = await SeedTenantAsync(eventInWindow: true);
        var payload = Payload($"office@{seed.Slug}.in.paddockside.com.au", "someone@partner.test", "Race book for Saturday", "Attached.");

        await InboundAsync(payload);
        await ProcessQueueAsync();

        await using var check = db.ContextFor(seed.TenantId);
        var message = await check.InboundMessages.SingleAsync(m => m.ProviderMessageId == payload.MessageId);
        Assert.Equal(InboundState.Pending, message.State);
        Assert.Null(message.StreamItemId);

        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.Contains("Race book for Saturday", await coordinator.GetStringAsync("/api/inbound?state=Pending"));
    }

    [Fact]
    public async Task A_token_sent_to_another_tenants_subdomain_is_not_honoured()
    {
        var other = await SeedTenantAsync(eventInWindow: true);
        var messageId = await PostToOwnersAsync(other, "Private to this tenant.");
        var token = await TokenForAsync(other, messageId, other.AnnId);

        // The right token, but at tenant A's subdomain: it is stored and matched inside tenant A, where it does not exist.
        var payload = Payload($"r-{token}@tenanta.in.paddockside.com.au", "ann@x.test", "Re: private", "Hello");
        await InboundAsync(payload);
        await ProcessQueueAsync();

        await using var a = db.ContextFor(db.A.TenantId);
        Assert.Equal(InboundState.Pending, (await a.InboundMessages.SingleAsync(m => m.ProviderMessageId == payload.MessageId)).State);
        await using var owner = db.ContextFor(other.TenantId);
        Assert.False(await owner.StreamItems.AnyAsync(i => i.InReplyToId == messageId));
    }

    [Fact]
    public async Task Auto_replies_and_bounces_are_ignored_and_spam_is_held_for_admins()
    {
        var seed = await SeedTenantAsync(eventInWindow: true);
        var to = $"bel-esprit@{seed.Slug}.in.paddockside.com.au";
        var outOfOffice = Payload(to, seed.TomEmail, "Automatic reply: Bel Esprit", "I am away.", headers: [("Auto-Submitted", "auto-replied")]);
        var bounce = Payload(to, "MAILER-DAEMON@mx.test", "Delivery Status Notification (Failure)", "Could not deliver.");
        var spam = Payload(to, "winner@lottery.test", "You have won", "Click here.", headers: [("X-Spam-Status", "Yes"), ("X-Spam-Score", "9.2")]);

        foreach (var payload in new[] { outOfOffice, bounce, spam }) await InboundAsync(payload);
        await ProcessQueueAsync();

        await using var check = db.ContextFor(seed.TenantId);
        async Task<InboundMessage> Get(PostmarkPayload p) => await check.InboundMessages.SingleAsync(m => m.ProviderMessageId == p.MessageId);
        Assert.Equal(InboundState.Ignored, (await Get(outOfOffice)).State);
        Assert.Equal(InboundState.Ignored, (await Get(bounce)).State);
        Assert.Equal(InboundState.Held, (await Get(spam)).State);
        Assert.False(await check.StreamItems.AnyAsync(i => i.HorseId == seed.HorseId && i.Direction == StreamItemDirection.Inbound));

        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        using var admin = await Api.SignedInAsync(seed.TenantId, MemberRole.TenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/inbound?state=Held")).StatusCode);
        Assert.Contains("You have won", await admin.GetStringAsync("/api/inbound?state=Held"));
    }

    [Fact]
    public async Task The_same_email_delivered_twice_is_stored_once()
    {
        var seed = await SeedTenantAsync(eventInWindow: true);
        var payload = Payload($"bel-esprit@{seed.Slug}.in.paddockside.com.au", seed.TomEmail, "Twice", "Once.");

        Assert.Equal(HttpStatusCode.OK, (await InboundAsync(payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InboundAsync(payload)).StatusCode);
        await ProcessQueueAsync();

        await using var check = db.ContextFor(seed.TenantId);
        Assert.Equal(1, await check.InboundMessages.CountAsync(m => m.ProviderMessageId == payload.MessageId));
        Assert.Equal(1, await check.StreamItems.CountAsync(i => i.Title == "Twice"));
    }

    [Fact]
    public async Task Mail_for_no_tenant_is_acknowledged_and_not_stored()
    {
        var payload = Payload("hello@nobody.in.paddockside.com.au", "x@y.test", "Who?", "Nobody home.");

        Assert.Equal(HttpStatusCode.OK, (await InboundAsync(payload)).StatusCode);

        await using var any = db.ContextFor(null);
        Assert.False(await any.InboundMessages.IgnoreQueryFilters().AnyAsync(m => m.ProviderMessageId == payload.MessageId));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private sealed record PostmarkPayload(string MessageId, byte[] Bytes);

    private static object Attachment(string name, string contentType, byte[] content, string? contentId = null) =>
        new { Name = name, Content = Convert.ToBase64String(content), ContentType = contentType, ContentLength = content.Length, ContentID = contentId ?? "" };

    /// <summary>A payload shaped like Postmark's, serialised once so the stored copy can be compared byte for byte.</summary>
    private static PostmarkPayload Payload(string to, string from, string subject, string text, object[]? attachments = null, (string, string)[]? headers = null)
    {
        var messageId = Guid.NewGuid().ToString();
        var allHeaders = new List<(string Name, string Value)> { ("X-Mailer", "Test Mail 1.0"), ("Message-ID", $"<{messageId}@mail.test>") };
        allHeaders.AddRange(headers ?? []);
        var json = new
        {
            From = from,
            FromName = "Sender",
            FromFull = new { Email = from, Name = "Sender", MailboxHash = "" },
            To = to,
            ToFull = new[] { new { Email = to, Name = "", MailboxHash = "" } },
            OriginalRecipient = to,
            Subject = subject,
            MessageID = messageId,
            Date = "Tue, 6 Oct 2026 19:30:00 +1100",
            TextBody = text,
            HtmlBody = $"<p>{WebUtility.HtmlEncode(text)}</p>",
            StrippedTextReply = "",
            Headers = allHeaders.Select(h => new { h.Name, h.Value }).ToArray(),
            Attachments = attachments ?? [],
        };
        return new PostmarkPayload(messageId, JsonSerializer.SerializeToUtf8Bytes(json));
    }

    private async Task<HttpResponseMessage> InboundAsync(PostmarkPayload payload)
    {
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, InboundPath) { Content = new ByteArrayContent(payload.Bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiFactory.WebhookUsername}:{ApiFactory.WebhookPassword}")));
        return await client.SendAsync(request);
    }

    /// <summary>Does what the background processor does: drains the queue through the processor.</summary>
    private async Task ProcessQueueAsync()
    {
        var queue = Api.Services.GetRequiredService<IInboundQueue>();
        while ((await queue.ReceiveAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None)) is { Count: > 0 } batch)
        {
            foreach (var message in batch)
            {
                await using var scope = Api.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<InboundProcessor>().ProcessAsync(message.TenantId, message.InboundMessageId, CancellationToken.None);
                await queue.CompleteAsync(message, CancellationToken.None);
            }
        }
    }

    private async Task<InboundMessage> InboundFor(Guid tenantId, string providerMessageId)
    {
        await using var context = db.ContextFor(tenantId);
        return await context.InboundMessages.SingleAsync(m => m.ProviderMessageId == providerMessageId);
    }

    private async Task<string> TokenForAsync(Seed seed, Guid messageId, Guid partyId)
    {
        await using var context = db.ContextFor(seed.TenantId);
        return (await context.RoutingAddresses.SingleAsync(r => r.StreamItemId == messageId && r.PartyId == partyId)).Token;
    }

    private async Task<Guid> PostToOwnersAsync(Seed seed, string body)
    {
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var response = await coordinator.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = Array.Empty<Guid>(), body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Posted>())!.Id;
    }

    /// <summary>A tenant of its own: horse Bel Esprit owned by Ann, trainer Tom on file, one open race start.</summary>
    private async Task<Seed> SeedTenantAsync(bool eventInWindow)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Inbound {suffix}", $"in{suffix}");
        var syndicate = new HoldingEntity(tenant.Id, "Syndicate", HoldingEntityType.ManagedSyndicate);
        var ann = new Party(tenant.Id, "Ann Owner");
        var tom = new Party(tenant.Id, "Tom Trainer");
        ann.AddEmail($"ann-{suffix}@owners.test");
        tom.AddEmail($"tom-{suffix}@stable.test");
        var horse = new Horse(tenant.Id, "Bel Esprit", HorseNameKind.Registered, now.AddYears(-1));
        horse.OpenManagementPeriod(now.AddYears(-1));
        horse.AddInterest(ann, syndicate, 1000, now.AddYears(-1));
        var race = new Event(horse, "RaceStart", "Caulfield, Saturday", eventInWindow ? now.AddDays(5) : now.AddDays(-90));
        race.Open(now.AddDays(-10));

        await using var context = db.ContextFor(tenant.Id);
        context.AddRange(tenant, syndicate, ann, tom, horse, race);
        await context.SaveChangesAsync();
        return new Seed(tenant.Id, tenant.Slug, horse.Id, race.Id, ann.Id, tom.Id, $"tom-{suffix}@stable.test");
    }
}
