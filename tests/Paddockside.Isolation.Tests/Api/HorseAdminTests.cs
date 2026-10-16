using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Staff adding a horse and its owners by hand (build plan, sprint 4). Shares the sign-in tests' seed: Bel Esprit,
/// owned by Ann and Bob.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record NewHorse(Guid Id);

    private sealed record AddedOwner(Guid Id, bool IsNew);

    private sealed record OwnerRow(Guid PartyId, string Name, string? Email, string? Mobile, string Percent, bool IsHouse, string? Invited);

    private sealed record OwnerList(List<OwnerRow> Owners, string Allocated, decimal Unallocated);

    private sealed record HorseInbox(string Name, string? InboxAddress, int CurrentOwners);

    [Fact]
    public async Task A_manager_adds_a_horse_and_its_owners_who_are_invited_and_never_doubled()
    {
        var seed = await SeedAsync();
        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        var today = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        var created = await manager.PostApiAsync("/api/horses", new
        {
            name = "Snitzel Star", nameKind = "Registered", sex = "Filly", foaledYear = 2023, sire = "Snitzel", dam = "Starlet", managedFrom = today, houseShare = (decimal?)null,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var horseId = (await created.Content.ReadFromJsonAsync<NewHorse>())!.Id;
        Assert.NotNull((await manager.GetFromJsonAsync<HorseInbox>($"/api/horses/{horseId}"))!.InboxAddress); // its own address from the start
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostApiAsync("/api/horses",
            new { name = "Snitzel Star", nameKind = "Registered", sex = (string?)null, foaledYear = (int?)null, sire = (string?)null, dam = (string?)null, managedFrom = today, houseShare = (decimal?)null })).StatusCode);

        // Someone new: created, and invited to the portal with their first share.
        var carol = $"carol-{Guid.NewGuid():N}@owners.test";
        var added = await manager.PostApiAsync($"/api/horses/{horseId}/owners", new { name = "Carol Owner", email = carol.ToUpperInvariant(), mobile = (string?)null, percent = 12.5m, since = today });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.True((await added.Content.ReadFromJsonAsync<AddedOwner>())!.IsNew);
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == carol);

        // Someone already known by their email is the same person, not a second Ann.
        var ann = (await (await manager.PostApiAsync($"/api/horses/{horseId}/owners", new { name = "Ann O.", email = seed.AnnEmail, mobile = (string?)null, percent = 5m, since = today }))
            .Content.ReadFromJsonAsync<AddedOwner>())!;
        Assert.Equal((seed.AnnId, false), (ann.Id, ann.IsNew));

        var owners = (await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{horseId}/owners"))!;
        Assert.Equal(["Ann Owner", "Carol Owner"], owners.Owners.Select(o => o.Name));
        Assert.Equal(("12.5%", carol), (owners.Owners[1].Percent, owners.Owners[1].Email));
        Assert.Equal(("17.5% of 100% allocated", 82.5m), (owners.Allocated, owners.Unallocated));

        // No more than the whole horse; not twice; a share to one decimal place; someone who can be reached.
        object Owner(string name, string? email, decimal percent) => new { name, email, mobile = (string?)null, percent, since = today };
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner("Dan", "dan@owners.test", 90m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner("Carol", carol, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner("Dan", "dan@owners.test", 2.55m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner("Dan", null, 2m))).StatusCode);

        // A coordinator runs the day; who owns a horse is for managers and admins.
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostApiAsync($"/api/horses/{horseId}/owners", Owner("Dan", "dan@owners.test", 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostApiAsync($"/api/horses/{db.A.HorseId}/owners", Owner("Dan", "dan@owners.test", 2m))).StatusCode);
    }

    [Fact]
    public async Task A_new_horse_can_start_with_the_house_share_which_is_listed_first_and_never_invited()
    {
        var seed = await SeedAsync();
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var tenant = context.Tenants.Single();
            var houseParty = new Party(tenant.Id, "Owners House", PartyKind.Organisation);
            context.Parties.Add(houseParty);
            tenant.SetHouseParty(houseParty);
            await context.SaveChangesAsync();
        }

        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        var horseId = (await (await manager.PostApiAsync("/api/horses", new
        {
            name = "Lot 88", nameKind = "SaleLot", sex = "Colt", foaledYear = 2024, sire = (string?)null, dam = (string?)null,
            managedFrom = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"), houseShare = 10m,
        })).Content.ReadFromJsonAsync<NewHorse>())!.Id;

        var house = Assert.Single((await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{horseId}/owners"))!.Owners);
        Assert.Equal(("Owners House", "10%", true, (string?)null), (house.Name, house.Percent, house.IsHouse, house.Invited));

        // Syndication: the house sells part of its share to a client, who is invited like any owner.
        var dan = $"dan-{Guid.NewGuid():N}@owners.test";
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"/api/horses/{horseId}/owners/{house.PartyId}/transfer", new
        {
            recipients = new object[] { new { clientId = house.PartyId, percent = 7.5m }, new { name = "Dan Buyer", email = dan, percent = 2.5m } },
            on = (string?)null, access = "LockedOut",
        })).StatusCode);
        var owners = (await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{horseId}/owners"))!;
        Assert.Equal([("Owners House", "7.5%"), ("Dan Buyer", "2.5%")], owners.Owners.Select(o => (o.Name, o.Percent)));
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == dan);
    }
}
