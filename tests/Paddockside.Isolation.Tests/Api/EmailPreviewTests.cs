using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The operator's batch preview (David, 25 Nov 2026): every kind of email, in one business's look, to an address the
/// operator chooses. Made up, saved nowhere, and nobody else's to send.
/// </summary>
public sealed partial class OperatorTests
{
    private sealed record Option(string Key, string Label, string Group);

    private sealed record PreviewSent(int Sent, List<string> Problems);

    private sealed record Look(string Name, string? Colour, string? Footer);

    [Fact]
    public async Task An_operator_sends_a_business_a_batch_of_made_up_emails_in_its_look()
    {
        var (ops, operatorEmail) = await Api.SignedInOperatorAsync();
        var path = $"/api/ops/tenants/{db.A.TenantId}/email";
        var before = (await ops.GetFromJsonAsync<Look>($"{path}/look"))!;
        Assert.Equal(HttpStatusCode.OK, (await ops.PutApiAsync($"{path}/look", new { colour = "#224466", footer = "Tenant A Pty Ltd" })).StatusCode);

        var options = (await ops.GetFromJsonAsync<List<Option>>($"{path}/samples"))!;
        Assert.True(options.Count >= 15);
        Assert.Equal(options.Count, options.Select(o => o.Key).Distinct().Count());

        var to = $"preview-{Guid.NewGuid():N}@example.test";
        async Task<int[]> Counts()
        {
            await using var context = db.ContextFor(db.A.TenantId);
            return [await context.Horses.CountAsync(), await context.Parties.CountAsync(), await context.StreamItems.CountAsync(), await context.Mailouts.CountAsync(), await context.Deliveries.CountAsync()];
        }

        var counts = await Counts();
        var response = await ops.PostApiAsync($"{path}/preview", new { email = to });
        var result = (await response.Content.ReadFromJsonAsync<PreviewSent>())!;
        Assert.Equal(options.Count, result.Sent);
        Assert.Empty(result.Problems);
        Assert.Equal(counts, await Counts()); // nothing saved

        var sent = Api.Emails.Sent.Where(e => e.Email.ToAddress == to).Select(e => e.Email).ToList();
        Assert.Equal(options.Count, sent.Count);
        Assert.All(sent, e => Assert.StartsWith("[Preview ", e.Subject));
        Assert.All(sent, e => Assert.DoesNotContain("Tenant A Mare", e.HtmlBody + e.TextBody)); // none of the business's content
        Assert.All(sent, e => Assert.DoesNotContain("Tenant A Ann", e.HtmlBody + e.TextBody));
        Assert.All(sent, e => Assert.DoesNotContain("/my/link#", e.TextBody)); // no working sign-in link
        Assert.True(sent.Count(e => e.HtmlBody.Contains("background:#224466")) > 12);

        // Every link goes to the page that says it's a preview, and that page is there.
        var links = sent.SelectMany(e => System.Text.RegularExpressions.Regex.Matches(e.HtmlBody, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.StartsWith($"{ApiFactory.PortalBaseUrl}/email-preview", l));
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Contains("This is a preview", await anyone.GetStringAsync("/email-preview"));

        // Only the ones chosen.
        var one = (await (await ops.PostApiAsync($"{path}/preview", new { email = to, keys = new[] { "tickets" } })).Content.ReadFromJsonAsync<PreviewSent>())!;
        Assert.Equal(1, one.Sent);

        // The business can see it happened.
        await using (var context = db.ContextFor(db.A.TenantId))
            Assert.Contains(await context.AuditEntries.Where(a => a.Action == "email.previewed").Select(a => a.Summary).ToListAsync(), s => s.Contains(to));

        // A bad address, none chosen, no such business; and not staff's, even a tenant admin's, to send.
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostApiAsync($"{path}/preview", new { email = "a@b.test, c@d.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ops.PostApiAsync($"{path}/preview", new { email = to, keys = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ops.PostApiAsync($"/api/ops/tenants/{Guid.NewGuid()}/email/preview", new { email = to })).StatusCode);
        using var admin = await Api.SignedInAsync(db.A.TenantId, MemberRole.TenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostApiAsync($"{path}/preview", new { email = to })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{path}/samples")).StatusCode);

        await ops.PutApiAsync($"{path}/look", new { colour = before.Colour, footer = before.Footer }); // leave the shared fixture as it was
        Assert.Contains('@', operatorEmail);
    }
}
