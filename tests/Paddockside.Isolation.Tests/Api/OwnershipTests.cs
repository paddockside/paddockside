using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Ownership changes from the horse page (ownership-model.md scenarios 1–3): passing a share on, an owner leaving,
/// and the horse leaving management, with whoever stops owning shut out of the portal. Shares the sign-in tests'
/// seed: Bel Esprit, owned 50/50 by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    [Fact]
    public async Task A_share_is_passed_on_in_part_and_in_full_and_whoever_sells_up_is_locked_out()
    {
        var seed = await SeedAsync();
        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        var transfer = $"/api/horses/{seed.HorseId}/owners";

        // Bob keeps half of his share and passes the other half to someone new, who is invited.
        var cara = $"cara-{Guid.NewGuid():N}@owners.test";
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"{transfer}/{seed.BobId}/transfer", new
        {
            recipients = new object[] { new { clientId = seed.BobId, percent = 25m }, new { name = "Cara New", email = cara, percent = 25m } },
            on = (string?)null, access = "LockedOut",
        })).StatusCode);
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == cara);
        var owners = (await manager.GetFromJsonAsync<OwnerList>(transfer))!;
        Assert.Equal([("Ann Owner", "50%"), ("Bob Owner", "25%"), ("Cara New", "25%")], owners.Owners.Select(o => (o.Name, o.Percent)));

        // Ann sells all of hers to Bob: one line for him, 75%; she no longer sees the horse.
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.NotEmpty((await ann.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"{transfer}/{seed.AnnId}/transfer", new
        {
            recipients = new[] { new { clientId = seed.BobId, percent = 50m } }, on = (string?)null, access = "LockedOut",
        })).StatusCode);
        owners = (await manager.GetFromJsonAsync<OwnerList>(transfer))!;
        Assert.Equal([("Bob Owner", "75%"), ("Cara New", "25%")], owners.Owners.Select(o => (o.Name, o.Percent)));
        Assert.Empty((await ann.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/my/events/{seed.EventId}")).StatusCode);
        Assert.Equal("Past owner", (await manager.GetFromJsonAsync<ClientDetail>($"/api/clients/{seed.AnnId}"))!.Status);

        // The shares must add up to what they hold; not ahead of today; only someone who owns part; only a manager.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"{transfer}/{seed.BobId}/transfer",
            new { recipients = new[] { new { clientId = seed.AnnId, percent = 70m } }, on = (string?)null, access = "LockedOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"{transfer}/{seed.BobId}/transfer",
            new { recipients = new[] { new { clientId = seed.AnnId, percent = 75m } }, on = DateTimeOffset.UtcNow.AddDays(10).ToString("yyyy-MM-dd"), access = "LockedOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostApiAsync($"{transfer}/{seed.AnnId}/transfer",
            new { recipients = new[] { new { clientId = seed.BobId, percent = 50m } }, on = (string?)null, access = "LockedOut" })).StatusCode);
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostApiAsync($"{transfer}/{seed.BobId}/exit", new { toHouse = false, on = (string?)null, access = "LockedOut" })).StatusCode);
    }

    [Fact]
    public async Task An_owner_leaves_and_then_the_horse_leaves_management_and_everyone_stops()
    {
        var seed = await SeedAsync();
        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);

        // Ann leaves; there's no house share here, so hers can't go to it; left unallocated it can.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{seed.AnnId}/exit",
            new { toHouse = true, on = (string?)null, access = "ReadOnly" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{seed.AnnId}/exit",
            new { toHouse = false, on = (string?)null, access = "ReadOnly" })).StatusCode);
        var owners = (await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{seed.HorseId}/owners"))!;
        Assert.Equal(("Bob Owner", "50% of 100% allocated"), (owners.Owners.Single().Name, owners.Allocated));

        // Sold: Bob stops too; the horse and its history stay, no longer managed.
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/end-management", new { reason = "", on = (string?)null, access = "LockedOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/end-management",
            new { reason = "Sold to a stud in Victoria", on = (string?)null, access = "LockedOut" })).StatusCode);
        Assert.Empty((await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{seed.HorseId}/owners"))!.Owners);
        Assert.Equal("Past owner", (await manager.GetFromJsonAsync<ClientDetail>($"/api/clients/{seed.BobId}"))!.Status);
        using var bob = await SignInAsync(BobEmail(seed));
        Assert.Empty((await bob.GetFromJsonAsync<List<HomeHorse>>("/api/my/horses"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{seed.HorseId}/end-management", new { reason = "Again", on = (string?)null, access = "LockedOut" })).StatusCode);
    }
}
