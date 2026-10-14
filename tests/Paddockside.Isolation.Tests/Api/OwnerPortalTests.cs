using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The owner home, horse timeline and event page (search-reporting.md §3): unread counts per person per item,
/// "Contact {tenant}" landing in the stream for staff and that owner only, and other owners named only when the
/// tenant allows. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record HomeHorse(Guid Id, string Name, int Unread);

    private sealed record Card(Guid Id, string Status, int Unread);

    private sealed record Update(Guid ItemId, string Summary, bool Unread);

    private sealed record PortalItem(Guid Id, string Kind, string Body, string? Author, bool Unread, bool Mine);

    private sealed record PortalEvent(Card Event, List<PortalItem> Items, List<string>? CoOwners);

    [Fact]
    public async Task New_items_count_as_unread_until_the_owner_opens_the_event()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);

        // The tickets message and the (corrected) barrier draw: two new things, on the horse and on its race.
        Assert.Equal(2, (await ann.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!.Single().Unread);
        Assert.Equal(2, (await ann.GetFromJsonAsync<List<Card>>($"/api/my/horses/{seed.HorseId}/events"))!.Single(e => e.Id == seed.EventId).Unread);
        var page = await ann.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}");
        Assert.Equal(2, page!.Items.Count(i => i.Unread));

        Assert.Equal(HttpStatusCode.NoContent, (await ann.PostApiAsync($"/api/my/events/{seed.EventId}/seen", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PostApiAsync($"/api/my/events/{seed.EventId}/seen", new { })).StatusCode); // twice is fine

        Assert.Equal(0, (await ann.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!.Single().Unread);
        Assert.DoesNotContain((await ann.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.Items, i => i.Unread);
        Assert.DoesNotContain((await ann.GetFromJsonAsync<List<Update>>("/api/my/updates"))!, u => u.Unread);

        // Per person: Ann reading it doesn't mark it read for Bob.
        using var bob = await SignInAsync(BobEmail(seed));
        Assert.Equal(2, (await bob.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!.Single().Unread);

        // Another tenant's event cannot be marked.
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostApiAsync($"/api/my/events/{db.A.EventId}/seen", new { })).StatusCode);
    }

    [Fact]
    public async Task Contact_reaches_staff_on_the_horse_and_only_the_owner_who_wrote_it()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);

        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostApiAsync($"/api/my/events/{seed.EventId}/contact", new { body = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostApiAsync($"/api/my/events/{db.A.EventId}/contact", new { body = "Hello" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ann.PostApiAsync($"/api/my/events/{seed.EventId}/contact", new { body = "Can we visit the stable on Friday?" })).StatusCode);

        // Ann sees it as hers, and it isn't news to her.
        var mine = (await ann.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.Items.Single(i => i.Body == "Can we visit the stable on Friday?");
        Assert.True(mine.Mine);
        Assert.False(mine.Unread);
        Assert.Equal("You", mine.Author);
        Assert.DoesNotContain((await ann.GetFromJsonAsync<List<Update>>("/api/my/updates"))!, u => u.Summary.Contains("visit the stable"));

        // Staff see it on the race, from her, through the portal.
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var staffPage = await staff.GetStringAsync($"/api/events/{seed.EventId}");
        Assert.Contains("Can we visit the stable on Friday?", staffPage);
        Assert.Contains("Ann Owner", staffPage);

        // Bob never does.
        using var bob = await SignInAsync(BobEmail(seed));
        Assert.DoesNotContain((await bob.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.Items, i => i.Body.Contains("visit the stable"));
    }

    [Fact]
    public async Task Other_owners_are_named_only_when_the_tenant_allows_it()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.Null((await ann.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.CoOwners);

        await using (var context = db.ContextFor(seed.TenantId))
        {
            (await context.Tenants.SingleAsync()).OwnersSeeCoOwners = true;
            await context.SaveChangesAsync();
        }

        Assert.Equal(["Bob Owner"], (await ann.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.CoOwners);
    }

    private sealed record Choice(string Code, bool Important, string Email, string Text);

    private sealed record Choices(List<Choice> Categories, string? Email, string? Mobile, bool TextsAvailable);

    private sealed record Sent(Guid Id, int Recipients);

    [Fact]
    public async Task Owners_choose_what_they_are_sent_and_important_news_still_reaches_them()
    {
        var seed = await SeedAsync();
        using var ann = await SignInAsync(seed.AnnEmail);
        using var bob = await SignInAsync(BobEmail(seed));

        var page = (await ann.GetFromJsonAsync<Choices>("/api/my/notifications"))!;
        var results = page.Categories.Single(c => c.Code == "results");
        Assert.Equal((true, "on", "on"), (results.Important, results.Email, results.Text));        // Ann has both an address and a mobile
        Assert.Equal("unavailable", (await bob.GetFromJsonAsync<Choices>("/api/my/notifications"))!.Categories.Single(c => c.Code == "results").Text); // Bob has no mobile

        // Bob switches off stable updates by email; switching off his only way to hear results is refused.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutApiAsync("/api/my/notifications", new { category = "stable-updates", channel = "Email", enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PutApiAsync("/api/my/notifications", new { category = "results", channel = "Email", enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PutApiAsync("/api/my/notifications", new { category = "results", channel = "Text", enabled = true })).StatusCode); // no number
        // Ann keeps text on for results, so she may switch email off.
        Assert.Equal(HttpStatusCode.NoContent, (await ann.PutApiAsync("/api/my/notifications", new { category = "results", channel = "Email", enabled = false })).StatusCode);
        Assert.Equal("off", (await ann.GetFromJsonAsync<Choices>("/api/my/notifications"))!.Categories.Single(c => c.Code == "results").Email);

        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // A stable update: Ann by email; Bob switched those off, so only the portal has it for him.
        var update = await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Preferred", namedParties = (Guid[]?)null, body = "Worked well this morning." })).Content.ReadFromJsonAsync<Sent>();
        Assert.Equal(1, update!.Recipients);
        Assert.Contains((await bob.GetFromJsonAsync<PortalEvent>($"/api/my/events/{seed.EventId}"))!.Items, i => i.Body == "Worked well this morning.");

        // A result: Ann chose text only, but texts can't be sent yet, so it comes by email rather than not at all.
        var result = await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = "Result", channel = "Preferred", namedParties = (Guid[]?)null, body = "Second, beaten a nose." })).Content.ReadFromJsonAsync<Sent>();
        Assert.Equal(2, result!.Recipients);
    }

    /// <summary>Bob's address follows the seed's pattern: "bob-{suffix}@owners.test", the suffix ending the tenant name.</summary>
    private static string BobEmail(Seed seed) => $"bob-{seed.TenantName["Owners ".Length..]}@owners.test";
}
