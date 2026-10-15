using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Clients: current owners, past owners and prospects, their contact details, and lists of any mix of them. Shares the
/// sign-in tests' seed: Bel Esprit, owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record ClientRow(Guid Id, string Name, string? Email, string Status, string? Horses, bool SignedIn, List<string> Lists);

    private sealed record ClientHorse(Guid HorseId, string Name, string Share, string When);

    private sealed record ClientDetail(Guid Id, string Name, string? Email, string? Mobile, string? HowWeKnowThem, string Status, string? Invitation, bool CanResend,
        List<ClientHorse> CurrentHorses, List<ClientHorse> PastHorses);

    private sealed record ClientCreated(Guid Id);

    private sealed record ListCreated(Guid Id, string Name, int Count);

    private sealed record ListChanged(int Count);

    /// <summary>Pat owned part of Bel Esprit until two months ago: a past owner.</summary>
    private async Task<Guid> SeedPastOwnerAsync(Seed seed)
    {
        await using var context = db.ContextFor(seed.TenantId);
        var horse = await context.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).SingleAsync(h => h.Id == seed.HorseId);
        var pat = new Party(seed.TenantId, "Pat Former");
        pat.AddEmail($"pat-{Guid.NewGuid():N}@owners.test");
        context.Parties.Add(pat);
        var holding = await context.HoldingEntities.FirstAsync();
        var interest = horse.AddInterest(pat, holding, 100, DateTimeOffset.UtcNow.AddMonths(-10));
        horse.ExitInterest(interest.Id, DateTimeOffset.UtcNow.AddMonths(-2));
        await context.SaveChangesAsync();
        return pat.Id;
    }

    [Fact]
    public async Task Clients_are_current_owners_past_owners_and_prospects_and_lists_hold_any_mix()
    {
        var seed = await SeedAsync();
        var patId = await SeedPastOwnerAsync(seed);
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // A prospect, with how we know them; the same email again is the same person.
        var robEmail = $"rob-{Guid.NewGuid():N}@prospects.test";
        var created = await staff.PostApiAsync("/api/clients", new { name = "Rob Prospect", email = robEmail, mobile = (string?)null, howWeKnowThem = "Asked about shares at the Easter sale" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var robId = (await created.Content.ReadFromJsonAsync<ClientCreated>())!.Id;
        var again = await staff.PostApiAsync("/api/clients", new { name = "Robert", email = robEmail.ToUpperInvariant(), mobile = (string?)null, howWeKnowThem = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(robId.ToString(), await again.Content.ReadAsStringAsync());

        var all = (await staff.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!;
        Assert.Equal(["Ann Owner", "Bob Owner", "Pat Former", "Rob Prospect"], all.Select(c => c.Name));
        Assert.Equal(("Current owner", "Bel Esprit 50%"), (all[0].Status, all[0].Horses));
        Assert.Equal(("Past owner", "Owned Bel Esprit"), (all[2].Status, all[2].Horses));
        Assert.Equal(("Prospect", (string?)null), (all[3].Status, all[3].Horses));
        Assert.Equal([robId], (await staff.GetFromJsonAsync<List<ClientRow>>("/api/clients?status=Prospect"))!.Select(c => c.Id));
        Assert.Equal([patId], (await staff.GetFromJsonAsync<List<ClientRow>>("/api/clients?q=pat"))!.Select(c => c.Id));

        var pat = (await staff.GetFromJsonAsync<ClientDetail>($"/api/clients/{patId}"))!;
        Assert.Equal(("Bel Esprit", false), (pat.PastHorses.Single().Name, pat.CanResend));
        Assert.StartsWith("until", pat.PastHorses.Single().When);

        // "2026 prospective": one of each, once each; taking one off; the list's name once.
        var list = (await (await staff.PostApiAsync("/api/client-lists", new { name = "2026 prospective" })).Content.ReadFromJsonAsync<ListCreated>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostApiAsync("/api/client-lists", new { name = "2026 prospective" })).StatusCode);
        var added = await staff.PostApiAsync($"/api/client-lists/{list.Id}/add", new { clientIds = new[] { seed.AnnId, patId, robId } });
        Assert.Equal(3, (await added.Content.ReadFromJsonAsync<ListChanged>())!.Count);
        Assert.Equal(0, (await (await staff.PostApiAsync($"/api/client-lists/{list.Id}/add", new { clientIds = new[] { robId } })).Content.ReadFromJsonAsync<ListChanged>())!.Count);
        var onList = (await staff.GetFromJsonAsync<List<ClientRow>>($"/api/clients?list={list.Id}"))!;
        Assert.Equal(["Current owner", "Past owner", "Prospect"], onList.Select(c => c.Status));
        Assert.Contains("2026 prospective", onList[0].Lists);

        await staff.PostApiAsync($"/api/client-lists/{list.Id}/remove", new { clientIds = new[] { seed.AnnId } });
        Assert.Equal(2, (await staff.GetFromJsonAsync<List<ClientRow>>($"/api/clients?list={list.Id}"))!.Count);

        // Deleting the list keeps the clients.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/client-lists/{list.Id}/delete", new { })).StatusCode);
        Assert.Equal(4, (await staff.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!.Count);

        // Another tenant's client can't be put on a list here; a viewer can look but not change.
        var other = (await (await staff.PostApiAsync("/api/client-lists", new { name = "Other" })).Content.ReadFromJsonAsync<ListCreated>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostApiAsync($"/api/client-lists/{other.Id}/add", new { clientIds = new[] { db.A.PartyId } })).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.NotEmpty((await viewer.GetFromJsonAsync<List<ClientRow>>("/api/clients"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync("/api/clients", new { name = "X", email = "x@x.test", mobile = (string?)null, howWeKnowThem = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task A_mistyped_email_is_corrected_and_the_invitation_resent_to_the_right_address()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        await DispatchAsync(); // Bob's invitation goes to the address on file

        var bob = (await staff.GetFromJsonAsync<ClientDetail>($"/api/clients/{seed.BobId}"))!;
        Assert.StartsWith("Invited", bob.Invitation);
        Assert.True(bob.CanResend);

        // The address was wrong: corrected, the old one gone, not kept beside it.
        var right = $"bob.right-{Guid.NewGuid():N}@owners.test";
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PutApiAsync($"/api/clients/{seed.BobId}",
            new { name = "Bob Owner", email = right, mobile = "0412 345 678", howWeKnowThem = "Original syndicate member" })).StatusCode);
        bob = (await staff.GetFromJsonAsync<ClientDetail>($"/api/clients/{seed.BobId}"))!;
        Assert.Equal((right, "+61412345678", "Original syndicate member"), (bob.Email, bob.Mobile, bob.HowWeKnowThem));
        Assert.Empty((await staff.GetFromJsonAsync<List<ClientRow>>($"/api/clients?q={BobEmail(seed)}"))!);

        // Resent, to the right address.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/clients/{seed.BobId}/invitation", new { })).StatusCode);
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == right);

        // Someone else's address is refused; so is leaving them unreachable; a prospect has no portal to be invited to.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PutApiAsync($"/api/clients/{seed.BobId}",
            new { name = "Bob Owner", email = seed.AnnEmail, mobile = (string?)null, howWeKnowThem = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutApiAsync($"/api/clients/{seed.BobId}",
            new { name = "Bob Owner", email = "", mobile = "", howWeKnowThem = (string?)null })).StatusCode);
        var rob = (await (await staff.PostApiAsync("/api/clients", new { name = "Rob Prospect", email = $"rob-{Guid.NewGuid():N}@p.test", mobile = (string?)null, howWeKnowThem = (string?)null }))
            .Content.ReadFromJsonAsync<ClientCreated>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/clients/{rob}/invitation", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/clients/{db.A.PartyId}")).StatusCode);
    }
}
