using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The horse timeline and event page (non-functional.md §2): a member of tenant A asking for tenant B's ids gets
/// 404, by every route; and the compose rules hold on the server, not only in the compose box.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class EventEndpointTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record EventSummary(Guid Id, string Title);

    private sealed record EventPage(EventSummary Event, Guid HorseId, List<Item> Items, List<Owner> Owners);

    private sealed record Item(Guid Id, string Kind, string Scope, List<Recipient> Recipients);

    private sealed record Recipient(string Name, string Status);

    private sealed record Owner(Guid Id, string Name);

    private sealed record Posted(Guid Id, int Recipients);

    [Fact]
    public async Task Another_tenants_horse_and_event_are_not_found_by_any_route()
    {
        using var a = await Api.SignedInAsync(db.A.TenantId, MemberRole.Viewer);

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/horses/{db.B.HorseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/horses/{db.B.HorseId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/events/{db.B.EventId}")).StatusCode);
    }

    [Fact]
    public async Task Posting_to_another_tenants_event_is_not_found_and_writes_nothing()
    {
        using var a = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        await using var b = db.ContextFor(db.B.TenantId);
        var before = b.StreamItems.Count();

        var response = await a.PostApiAsync($"/api/events/{db.B.EventId}/items", Note("Should never land"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, b.StreamItems.Count());
    }

    [Fact]
    public async Task Own_horse_and_event_are_readable()
    {
        using var a = await Api.SignedInAsync(db.A.TenantId, MemberRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/horses/{db.A.HorseId}")).StatusCode);
        var events = await a.GetFromJsonAsync<List<EventSummary>>($"/api/horses/{db.A.HorseId}/events");
        Assert.Equal(db.A.EventId, Assert.Single(events!).Id);

        var page = await a.GetFromJsonAsync<EventPage>($"/api/events/{db.A.EventId}");
        Assert.Equal(db.A.HorseId, page!.HorseId);
        Assert.NotEmpty(page.Items);
    }

    [Fact]
    public async Task A_viewer_can_read_but_not_post()
    {
        using var viewer = await Api.SignedInAsync(db.A.TenantId, MemberRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/events/{db.A.EventId}/items", Note("Hello"))).StatusCode);
    }

    [Fact]
    public async Task A_message_to_owners_is_queued_for_current_owners_only()
    {
        using var coordinator = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);

        var response = await coordinator.PostApiAsync($"/api/events/{db.A.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Preferred", namedParties = Array.Empty<Guid>(), body = "Barrier 9." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var posted = await response.Content.ReadFromJsonAsync<Posted>();
        var page = await coordinator.GetFromJsonAsync<EventPage>($"/api/events/{db.A.EventId}");
        var item = page!.Items.Single(i => i.Id == posted!.Id);

        // Ann and Eve hold the horse now; Dee transferred out when the tenant was seeded, so is not told.
        Assert.Equal(2, posted!.Recipients);
        Assert.All(item.Recipients, r => Assert.Equal("Queued", r.Status));
        Assert.DoesNotContain(item.Recipients, r => r.Name.EndsWith("Dee", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Owners", "Trainer")]
    [InlineData("Trainer", "Owners")]
    [InlineData("InternalNote", "Owners")]
    [InlineData("Owners", "Internal")]
    public async Task The_server_refuses_a_scope_that_does_not_match_the_audience(string audience, string scope)
    {
        using var coordinator = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);

        var response = await coordinator.PostApiAsync($"/api/events/{db.A.EventId}/items",
            new { audience, scope, step = (string?)null, channel = "Preferred", namedParties = Array.Empty<Guid>(), body = "Mixed audiences" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Named_parties_must_be_current_owners()
    {
        using var coordinator = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);

        var response = await coordinator.PostApiAsync($"/api/events/{db.A.EventId}/items",
            new { audience = "Owners", scope = "NamedParties", step = (string?)null, channel = "Preferred", namedParties = new[] { db.B.PartyId }, body = "Psst" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object Note(string body) =>
        new { audience = "InternalNote", scope = "Internal", step = (string?)null, channel = "Preferred", namedParties = Array.Empty<Guid>(), body };
}
