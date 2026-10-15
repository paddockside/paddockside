using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Facts staff record by hand and correct (build plan, sprint 3: "manual facts and the corrected marker"). Shares the
/// sign-in tests' seed: Bel Esprit, owned by Ann and Bob, with a Caulfield race start.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record Template(string? Step, string Label, string Title, List<string> Fields);

    private sealed record WithTemplates(List<Template> FactTemplates, List<string> FactSources);

    private sealed record Recorded(Guid Id, int Recipients);

    private sealed record OwnerFactField(string Label, string Value, string? Was);

    private sealed record OwnerFact(Guid Id, string Kind, string? Title, List<OwnerFactField> Fields, string? Corrected);

    private sealed record OwnerFacts(List<OwnerFact> Items);

    [Fact]
    public async Task Staff_record_an_acceptance_tell_the_owners_and_correct_the_barrier()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // A race start offers its fact steps, with their usual fields, and somewhere to start from scratch.
        var page = (await staff.GetFromJsonAsync<WithTemplates>($"/api/events/{seed.EventId}"))!;
        var acceptance = page.FactTemplates.Single(t => t.Step == "ACC_FACT");
        Assert.Equal("Acceptance", acceptance.Title);
        Assert.Equal(["Race", "Barrier", "Weight", "Jockey"], acceptance.Fields);
        Assert.Contains(page.FactTemplates, t => t.Step is null);
        Assert.Contains("Racing Victoria", page.FactSources);

        // Recorded and told: it's important news, so both owners are sent it, and it reads as words.
        var posted = await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", new
        {
            step = "ACC_FACT", title = "Acceptance", source = "Racing Victoria", scope = "Owners", tell = true,
            fields = new[] { new { label = "Race", value = "Caulfield R4, 1200 m" }, new { label = "Barrier", value = "4" }, new { label = "Jockey", value = " " } },
        });
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var recorded = (await posted.Content.ReadFromJsonAsync<Recorded>())!;
        Assert.True(recorded.Recipients >= 2);
        await DispatchAsync();
        var told = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.Metadata.ContainsKey("deliveryId")).Email;
        Assert.Contains("Bel Esprit: Acceptance", told.Subject);
        Assert.Contains("Barrier: 4", told.TextBody);

        using var ann = await SignInAsync(seed.AnnEmail);
        var fact = (await ann.GetFromJsonAsync<OwnerFacts>($"/api/my/events/{seed.EventId}"))!.Items.Single(i => i.Id == recorded.Id);
        Assert.Equal(["Race", "Barrier"], fact.Fields.Select(f => f.Label)); // the blank jockey isn't recorded

        // The barrier is redrawn: corrected, the old value kept and shown, owners told what changed.
        var corrected = await staff.PostApiAsync($"/api/items/{recorded.Id}/correction", new
        {
            fields = new[] { new { label = "Race", value = "Caulfield R4, 1200 m" }, new { label = "Barrier", value = "9" } },
            note = "redraw after a scratching", tell = true,
        });
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        var correctedId = (await corrected.Content.ReadFromJsonAsync<Recorded>())!.Id;
        await DispatchAsync();
        Assert.Contains("Corrected: barrier 4 → 9 (redraw after a scratching).", Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.Metadata.ContainsKey("deliveryId")).Email.TextBody);

        var items = (await ann.GetFromJsonAsync<OwnerFacts>($"/api/my/events/{seed.EventId}"))!.Items;
        Assert.DoesNotContain(items, i => i.Id == recorded.Id); // shown once, as corrected
        var now = items.Single(i => i.Id == correctedId);
        Assert.NotNull(now.Corrected);
        Assert.Equal(new OwnerFactField("Barrier", "9", "4"), now.Fields.Single(f => f.Label == "Barrier"));

        // Only the latest version can be corrected, and only by changing something.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync($"/api/items/{recorded.Id}/correction",
            new { fields = new[] { new { label = "Barrier", value = "1" } }, note = (string?)null, tell = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/items/{correctedId}/correction",
            new { fields = new[] { new { label = "Race", value = "Caulfield R4, 1200 m" }, new { label = "Barrier", value = "9" } }, note = (string?)null, tell = false })).StatusCode);
    }

    [Fact]
    public async Task A_staff_only_fact_stays_with_staff_and_nonsense_is_refused()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        object Fact(string scope, bool tell, string title = "Vet check", string source = "The trainer", string? step = null, string value = "Sound") =>
            new { step, title, source, scope, tell, fields = new[] { new { label = "Finding", value } } };

        var internalFact = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Internal", tell: false))).Content.ReadFromJsonAsync<Recorded>())!;
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.DoesNotContain((await ann.GetFromJsonAsync<OwnerFacts>($"/api/my/events/{seed.EventId}"))!.Items, i => i.Id == internalFact.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Internal", tell: true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Owners", false, value: "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Owners", false, title: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Owners", false, source: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Owners", false, step: "VET_BOOKED"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostApiAsync($"/api/items/{seed.MessageId}/correction",
            new { fields = new[] { new { label = "Barrier", value = "1" } }, note = (string?)null, tell = false })).StatusCode);

        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/events/{seed.EventId}/facts", Fact("Owners", false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostApiAsync($"/api/events/{db.A.EventId}/facts", Fact("Owners", false))).StatusCode);
    }
}
