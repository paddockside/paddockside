using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Outbound email end to end, short of Postmark itself (messaging-channels.md §1–§2): compose, recipients at send
/// time, a routing token each, the email as sent, and Postmark's webhooks updating the delivery — including that a
/// webhook cannot touch another tenant's delivery.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed partial class OutboundEmailTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    [GeneratedRegex("^r-(?<token>[23456789abcdefghjkmnpqrstuvwxyz]{12})@(?<slug>[a-z0-9]+)\\.in\\.paddockside\\.com\\.au$")]
    private static partial Regex ReplyAddress();

    private sealed record Posted(Guid Id, int Recipients);

    [Fact]
    public async Task Each_owner_gets_one_email_with_their_own_reply_address()
    {
        var messageId = await PostToOwnersAsync(db.A, "Barrier 9 for Saturday.\n\nTickets <limited> & first in, first served.");
        await DispatchAsync();

        var sent = Api.Emails.Sent.Where(s => s.Email.Metadata["deliveryId"] is var id && DeliveryIdsFor(messageId).Contains(Guid.Parse(id))).ToList();
        await using var a = db.ContextFor(db.A.TenantId);
        var tenant = await a.Tenants.SingleAsync();

        // Ann and Eve own the horse; Dee transferred out, so is not emailed.
        Assert.Equal(["ann@" + tenant.Slug + ".test", "eve@" + tenant.Slug + ".test"], sent.Select(s => s.Email.ToAddress).Order());
        Assert.All(sent, s =>
        {
            Assert.Equal("updates@mail.paddockside.com.au", s.Email.FromAddress);
            Assert.Equal(tenant.Name, s.Email.FromName);
            var reply = ReplyAddress().Match(s.Email.ReplyTo);
            Assert.True(reply.Success, s.Email.ReplyTo);
            Assert.Equal(tenant.Slug, reply.Groups["slug"].Value);
            Assert.Contains("Tickets &lt;limited&gt; &amp; first in", s.Email.HtmlBody);   // escaped, never raw HTML
            Assert.DoesNotContain("<limited>", s.Email.HtmlBody);
            Assert.Contains("Tickets <limited> & first in", s.Email.TextBody);           // plain-text alternative
            Assert.Contains($"Your reply goes to {tenant.Name} only", s.Email.TextBody);
            Assert.Contains(tenant.Name, s.Email.HtmlBody);
        });
        Assert.Equal(2, sent.Select(s => s.Email.ReplyTo).Distinct().Count());

        // Each token is stored as a routing address pointing at that recipient and message.
        var deliveries = await a.Deliveries.Where(d => d.StreamItemId == messageId).ToListAsync();
        var tokens = await a.RoutingAddresses.Where(r => r.StreamItemId == messageId).ToListAsync();
        Assert.All(deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.Sent, d.Status);
            var token = tokens.Single(t => t.Id == d.RoutingAddressId);
            Assert.Equal(d.PartyId, token.PartyId);
            Assert.Contains(sent, s => s.Email.ReplyTo == $"r-{token.Token}@{tenant.Slug}.in.paddockside.com.au" && s.MessageId == d.ProviderMessageId);
        });
    }

    [Fact]
    public async Task An_owner_who_exits_after_the_message_is_queued_is_not_emailed()
    {
        // Ann and Eve own the horse.
        var (tenantId, eventId, ann, eve) = await SeedTenantAsync();
        var messageId = await PostToOwnersAsync(tenantId, eventId, "Queued before the exit.");

        // Eve sells to Ann before the background sender gets to it.
        await using (var a = db.ContextFor(tenantId))
        {
            var horse = await a.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
                .SingleAsync(h => h.Interests.Any(i => i.PartyId == eve));
            var buyer = await a.Parties.SingleAsync(p => p.Id == ann);
            var eveInterest = horse.Interests.Single(i => i.PartyId == eve && i.IsActive);
            horse.TransferInterest(eveInterest.Id, DateTimeOffset.UtcNow, [(buyer, eveInterest.Units)]);
            await a.SaveChangesAsync();
        }

        await DispatchAsync();

        await using var check = db.ContextFor(tenantId);
        var deliveries = await check.Deliveries.Where(d => d.StreamItemId == messageId).ToListAsync();
        Assert.Equal(DeliveryStatus.Sent, deliveries.Single(d => d.PartyId == ann).Status);
        var eveDelivery = deliveries.Single(d => d.PartyId == eve);
        Assert.Equal(DeliveryStatus.Suppressed, eveDelivery.Status);
        Assert.Contains("no longer an owner", eveDelivery.Note);
        Assert.DoesNotContain(Api.Emails.Sent, s => s.Email.Metadata["deliveryId"] == eveDelivery.Id.ToString());
    }

    [Fact]
    public async Task An_inactive_address_bounces_and_is_not_tried_again()
    {
        await using var b = db.ContextFor(db.B.TenantId);
        var tenant = await b.Tenants.SingleAsync();
        Api.Emails.Inactive.Add($"ann@{tenant.Slug}.test");

        var messageId = await PostToOwnersAsync(db.B, "First message.");
        await DispatchAsync();

        await using var check = db.ContextFor(db.B.TenantId);
        var annDelivery = await check.Deliveries.SingleAsync(d => d.StreamItemId == messageId && d.Address == $"ann@{tenant.Slug}.test");
        Assert.Equal(DeliveryStatus.Bounced, annDelivery.Status);
        var ann = await check.Parties.SingleAsync(p => p.Id == annDelivery.PartyId);
        Assert.False(ann.PrimaryEmail!.IsDeliverable);

        Api.Emails.Inactive.Clear();
        var second = await PostToOwnersAsync(db.B, "Second message.");
        await DispatchAsync();

        await using var again = db.ContextFor(db.B.TenantId);
        var annSecond = await again.Deliveries.SingleAsync(d => d.StreamItemId == second && d.PartyId == ann.Id);
        Assert.Equal(DeliveryStatus.Suppressed, annSecond.Status);
        Assert.Contains("bounced", annSecond.Note);
    }

    [Fact]
    public async Task Webhooks_need_the_credentials()
    {
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        var none = await client.PostAsJsonAsync(PostmarkWebhooksPath, new { RecordType = "Delivery" });
        using var wrong = new HttpRequestMessage(HttpMethod.Post, PostmarkWebhooksPath) { Content = JsonContent.Create(new { RecordType = "Delivery" }, options: Postmark) };
        wrong.Headers.Authorization = Basic(ApiFactory.WebhookUsername, "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task Delivery_and_hard_bounce_webhooks_update_the_delivery()
    {
        var messageId = await PostToOwnersAsync(db.A, "Webhook test.");
        await DispatchAsync();
        await using var a = db.ContextFor(db.A.TenantId);
        var deliveries = await a.Deliveries.Where(d => d.StreamItemId == messageId).OrderBy(d => d.Address).ToListAsync();
        var (first, second) = (deliveries[0], deliveries[1]);

        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(new { RecordType = "Delivery", MessageID = first.ProviderMessageId, DeliveredAt = "2026-10-06T08:00:00Z", Metadata = Meta(db.A.TenantId, first.Id) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(new { RecordType = "Bounce", Type = "HardBounce", TypeCode = 1, Description = "Mailbox not found", MessageID = second.ProviderMessageId, Email = second.Address, BouncedAt = "2026-10-06T08:01:00Z", Inactive = true, Metadata = Meta(db.A.TenantId, second.Id) })).StatusCode);

        await using var check = db.ContextFor(db.A.TenantId);
        var after = await check.Deliveries.Where(d => d.StreamItemId == messageId).OrderBy(d => d.Address).ToListAsync();
        Assert.Equal(DeliveryStatus.Delivered, after[0].Status);
        Assert.Equal(DeliveryStatus.Bounced, after[1].Status);
        Assert.Contains("Mailbox not found", after[1].Note);
        var bounced = await check.Parties.SingleAsync(p => p.Id == after[1].PartyId);
        Assert.False(bounced.PrimaryEmail!.IsDeliverable);

        bounced.ClearUndeliverable(bounced.PrimaryEmail.Value); // leave the shared fixture as it was
        await check.SaveChangesAsync();
    }

    [Fact]
    public async Task A_webhook_cannot_change_a_delivery_it_does_not_match()
    {
        var messageId = await PostToOwnersAsync(db.B, "Tamper test.");
        await DispatchAsync();
        await using var b = db.ContextFor(db.B.TenantId);
        var target = await b.Deliveries.FirstAsync(d => d.StreamItemId == messageId);

        // Right delivery, wrong Postmark message id.
        await WebhookAsync(new { RecordType = "Bounce", Type = "HardBounce", TypeCode = 1, MessageID = Guid.NewGuid().ToString(), Metadata = Meta(db.B.TenantId, target.Id) });
        // Right message id, but claiming tenant A: tenant B's delivery is invisible from there.
        await WebhookAsync(new { RecordType = "Bounce", Type = "HardBounce", TypeCode = 1, MessageID = target.ProviderMessageId, Metadata = Meta(db.A.TenantId, target.Id) });

        await using var check = db.ContextFor(db.B.TenantId);
        Assert.Equal(DeliveryStatus.Sent, (await check.Deliveries.SingleAsync(d => d.Id == target.Id)).Status);
    }

    [Fact]
    public void The_email_renderer_escapes_html_and_always_has_a_text_part()
    {
        var tenant = new Tenant("Riverbend & Co", "riverbend");
        var horse = new Horse(tenant.Id, "Night <Owl>", HorseNameKind.Registered, DateTimeOffset.UtcNow);
        var owner = new Party(tenant.Id, "Sam Lee");
        var message = StreamItem.Message(horse, null, "Result", "<script>alert(1)</script>", StreamItemScope.Owners, DateTimeOffset.UtcNow, "Kate");

        var email = new OwnerEmailRenderer().Render(tenant, horse, null, message, owner);

        Assert.Equal("Night <Owl>: Result", email.Subject);
        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html);
        Assert.Contains("Riverbend &amp; Co", email.Html);
        Assert.Contains("Hi Sam,", email.Text);
        Assert.Contains("<script>alert(1)</script>", email.Text);
        Assert.Contains("You are receiving this because you are an owner of Night <Owl>.", email.Text);
    }

    private const string PostmarkWebhooksPath = "/api/webhooks/postmark";

    /// <summary>Postmark sends PascalCase names; JsonContent would otherwise camelCase them.</summary>
    private static readonly JsonSerializerOptions Postmark = new();

    /// <summary>A tenant of its own, so changing its ownership leaves the shared seed alone.</summary>
    private async Task<(Guid TenantId, Guid EventId, Guid Ann, Guid Eve)> SeedTenantAsync()
    {
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Exit {suffix}", $"exit{suffix}");
        var tenantId = tenant.Id;
        await using var context = db.ContextFor(tenantId);
        var syndicate = new HoldingEntity(tenantId, "Exit Syndicate", HoldingEntityType.ManagedSyndicate);
        var ann = new Party(tenantId, $"Exit Ann {suffix}");
        var eve = new Party(tenantId, $"Exit Eve {suffix}");
        ann.AddEmail($"ann-{suffix}@exit.test");
        eve.AddEmail($"eve-{suffix}@exit.test");
        var horse = new Horse(tenantId, $"Exit Mare {suffix}", HorseNameKind.Registered, at);
        horse.OpenManagementPeriod(at);
        horse.AddInterest(ann, syndicate, 500, at);
        horse.AddInterest(eve, syndicate, 500, at);
        var raceStart = new Event(horse, "RaceStart", "Exit race", at.AddDays(40));
        raceStart.Open(at);
        context.AddRange(tenant, syndicate, ann, eve, horse, raceStart);
        await context.SaveChangesAsync();
        return (tenantId, raceStart.Id, ann.Id, eve.Id);
    }

    private Task<Guid> PostToOwnersAsync(SeededTenant tenant, string body) => PostToOwnersAsync(tenant.TenantId, tenant.EventId, body);

    private async Task<Guid> PostToOwnersAsync(Guid tenantId, Guid eventId, string body)
    {
        using var coordinator = await Api.SignedInAsync(tenantId, MemberRole.Coordinator);
        var response = await coordinator.PostApiAsync($"/api/events/{eventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = Array.Empty<Guid>(), body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Posted>())!.Id;
    }

    private async Task DispatchAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchAsync(CancellationToken.None);
    }

    private HashSet<Guid> DeliveryIdsFor(Guid messageId)
    {
        using var a = db.ContextFor(db.A.TenantId);
        return a.Deliveries.Where(d => d.StreamItemId == messageId).Select(d => d.Id).ToHashSet();
    }

    private async Task<HttpResponseMessage> WebhookAsync(object payload)
    {
        using var client = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Post, PostmarkWebhooksPath) { Content = JsonContent.Create(payload, options: Postmark) };
        request.Headers.Authorization = Basic(ApiFactory.WebhookUsername, ApiFactory.WebhookPassword);
        return await client.SendAsync(request);
    }

    private static Dictionary<string, string> Meta(Guid tenantId, Guid deliveryId) =>
        new() { ["tenantId"] = tenantId.ToString(), ["deliveryId"] = deliveryId.ToString() };

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
}
