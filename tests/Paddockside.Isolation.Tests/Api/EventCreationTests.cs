using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Staff creating events by hand (event-library.md §2, §5): open or draft, one per type per date per horse, and
/// nothing sent from a draft until someone opens it. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record CreatedEvent(Guid Id);

    private sealed record EventSummary(Guid Id, string Title, string Status, string KeyDate);

    private sealed record StaffEventPage(EventSummary Event);

    [Fact]
    public async Task Staff_create_an_open_race_start_owners_see_it_and_a_second_on_the_same_day_is_refused()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        var types = await staff.GetFromJsonAsync<List<EventTypeOption>>("/api/event-types");
        Assert.Contains(types!, t => t.Code == "RaceStart" && t.KeyDateLabel == "Race day");

        var day = DateTimeOffset.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        var created = await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "RaceStart", title = "Randwick, Race 6", keyDate = day, open = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedEvent>())!.Id;
        Assert.Equal("Open", (await staff.GetFromJsonAsync<StaffEventPage>($"/api/events/{id}"))!.Event.Status);

        // The owner sees it on their horse straight away (an open race with nothing posted yet).
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.Contains(await ann.GetFromJsonAsync<List<EventSummary>>($"/api/my/horses/{seed.HorseId}/events") ?? [], e => e.Id == id);

        // A second race start on the same day for the same horse is a mistake, not a second race.
        var again = await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "RaceStart", title = "Randwick R6 again", keyDate = day, open = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already has a race start", await again.Content.ReadAsStringAsync());

        // A race needs its day; a title is required; a viewer can't create.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "RaceStart", title = "No date", keyDate = (string?)null, open = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "General", title = " ", keyDate = (string?)null, open = true })).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/horses/{seed.HorseId}/events", new { type = "General", title = "Spell", keyDate = (string?)null, open = true })).StatusCode);
    }

    [Fact]
    public async Task A_draft_is_hidden_from_owners_and_sends_nothing_until_it_is_opened()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var id = (await (await staff.PostApiAsync($"/api/horses/{seed.HorseId}/events",
            new { type = "Veterinary", title = "Scope after trackwork", keyDate = DateTimeOffset.UtcNow.AddDays(3).ToString("yyyy-MM-dd"), open = false }))
            .Content.ReadFromJsonAsync<CreatedEvent>())!.Id;

        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/my/events/{id}")).StatusCode);

        // Nothing goes to owners from a draft; a staff note is fine.
        var toOwners = await staff.PostApiAsync($"/api/events/{id}/items", new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Preferred", namedParties = (Guid[]?)null, body = "Booked for Tuesday." });
        Assert.Equal(HttpStatusCode.BadRequest, toOwners.StatusCode);
        Assert.Contains("still a draft", await toOwners.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await staff.PostApiAsync($"/api/events/{id}/items",
            new { audience = "InternalNote", scope = "Internal", step = (string?)null, channel = "Preferred", namedParties = (Guid[]?)null, body = "Vet rang." })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/events/{id}/open", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync($"/api/events/{id}/open", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync($"/api/my/events/{id}")).StatusCode);
    }

    private sealed record EventTypeOption(string Code, string Name, string KeyDateLabel, string TitleExample);
}
