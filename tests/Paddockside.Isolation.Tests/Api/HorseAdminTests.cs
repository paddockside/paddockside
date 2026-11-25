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

    private sealed record AddedOwner(Guid Id, string Name, bool Invited);

    /// <summary>A client added on the Clients page, to give a share to.</summary>
    private static async Task<Guid> ClientAsync(HttpClient staff, string name, string? email, string? mobile = null) =>
        (await (await staff.PostApiAsync("/api/clients", new { name, email, mobile, howWeKnowThem = "Asked about shares" })).Content.ReadFromJsonAsync<NewHorse>())!.Id;

    private sealed record OwnerRow(Guid PartyId, string Name, string? Email, string? Mobile, string Percent, bool IsHouse, string? Invited);

    private sealed record OwnerList(List<OwnerRow> Owners, string Allocated, decimal Unallocated, string? Warning = null, bool Over = false, Guid? HouseId = null);

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

        // A client with their first share is invited to the portal.
        var carol = $"carol-{Guid.NewGuid():N}@owners.test";
        var carolId = await ClientAsync(manager, "Carol Owner", carol);
        var added = await manager.PostApiAsync($"/api/horses/{horseId}/owners", new { clientId = carolId, percent = 12.5m, since = today });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(new AddedOwner(carolId, "Carol Owner", true), await added.Content.ReadFromJsonAsync<AddedOwner>());
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == carol);

        // An owner of another horse is the same client, not a second Ann, and isn't invited again.
        var ann = (await (await manager.PostApiAsync($"/api/horses/{horseId}/owners", new { clientId = seed.AnnId, percent = 5m, since = today }))
            .Content.ReadFromJsonAsync<AddedOwner>())!;
        Assert.Equal((seed.AnnId, false), (ann.Id, ann.Invited));

        var owners = (await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{horseId}/owners"))!;
        Assert.Equal(["Ann Owner", "Carol Owner"], owners.Owners.Select(o => o.Name));
        Assert.Equal(("12.5%", carol), (owners.Owners[1].Percent, owners.Owners[1].Email));
        Assert.Equal(("17.5% of 100% allocated", 82.5m), (owners.Allocated, owners.Unallocated));
        Assert.Equal(("82.5% has no owner yet.", false), (owners.Warning, owners.Over)); // a note, not an alarm

        // No more than the whole horse; not twice; a share to one decimal place.
        var danId = await ClientAsync(manager, "Dan Client", $"dan-{Guid.NewGuid():N}@owners.test");
        object Owner(Guid clientId, decimal percent) => new { clientId, percent, since = today };
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner(danId, 90m))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner(carolId, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostApiAsync($"/api/horses/{horseId}/owners", Owner(danId, 2.55m))).StatusCode);

        // A coordinator runs the day; who owns a horse is for managers and admins.
        using var coordinator = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostApiAsync($"/api/horses/{horseId}/owners", Owner(danId, 2m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostApiAsync($"/api/horses/{db.A.HorseId}/owners", Owner(danId, 2m))).StatusCode);
    }

    [Fact]
    public async Task An_owner_is_only_ever_one_of_the_clients()
    {
        var seed = await SeedAsync();
        Guid houseId, trainerId, listedVetId, unreachableId;
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var tenant = context.Tenants.Single();
            var house = new Party(tenant.Id, "Owners House", PartyKind.Organisation);
            var (trainer, listedVet, unreachable) = (new Party(tenant.Id, "Terry Trainer"), new Party(tenant.Id, "Vera Vet"), new Party(tenant.Id, "Una Unreachable"));
            trainer.AddEmail($"terry-{Guid.NewGuid():N}@stable.test");
            listedVet.AddEmail($"vera-{Guid.NewGuid():N}@vets.test");
            context.Parties.AddRange(house, trainer, listedVet, unreachable);
            tenant.SetHouseParty(house);
            var horse = context.Horses.Single(h => h.Id == seed.HorseId);
            context.HorseContacts.Add(HorseContact.Tag(horse, trainer, ContactRole.Trainer, [], DateTimeOffset.UtcNow)!);
            context.HorseContacts.Add(HorseContact.Tag(horse, listedVet, ContactRole.Vet, [], DateTimeOffset.UtcNow)!);
            var list = new ClientList(tenant.Id, "2026 prospective", DateTimeOffset.UtcNow);
            list.Add([listedVet], DateTimeOffset.UtcNow);
            context.ClientLists.Add(list);
            await context.SaveChangesAsync();
            (houseId, trainerId, listedVetId, unreachableId) = (house.Id, trainer.Id, listedVet.Id, unreachable.Id);
        }

        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        await manager.PostApiAsync($"/api/horses/{seed.HorseId}/owners/{seed.AnnId}/exit", new { toHouse = false, on = (string?)null, access = "LockedOut" });
        var owners = $"/api/horses/{seed.HorseId}/owners";
        async Task<HttpStatusCode> AddAsync(object body) => (await manager.PostApiAsync(owners, body)).StatusCode;
        async Task<HttpStatusCode> PassAsync(object recipient) => (await manager.PostApiAsync($"{owners}/{seed.BobId}/transfer",
            new { recipients = new[] { recipient }, on = (string?)null, access = "LockedOut" })).StatusCode;
        int parties;
        await using (var context = db.ContextFor(seed.TenantId)) parties = context.Parties.Count();

        // A typed name and email is refused, adding and passing on; so are the trainer, another business's client, and nobody.
        Assert.Equal(HttpStatusCode.BadRequest, await AddAsync(new { name = "Typed In", email = "typed@owners.test", percent = 5m }));
        Assert.Equal(HttpStatusCode.BadRequest, await PassAsync(new { name = "Typed In", email = "typed@owners.test", percent = 50m }));
        foreach (var notAClient in new[] { trainerId, db.A.PartyId, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.BadRequest, await AddAsync(new { clientId = notAClient, percent = 5m }));
            Assert.Equal(HttpStatusCode.BadRequest, await PassAsync(new { clientId = notAClient, percent = 50m }));
        }

        Assert.Equal(HttpStatusCode.BadRequest, await AddAsync(new { clientId = houseId, percent = 5m })); // the house has its own share, it isn't a client
        Assert.Equal(HttpStatusCode.BadRequest, await AddAsync(new { clientId = unreachableId, percent = 5m })); // no email or mobile
        Assert.Equal(HttpStatusCode.BadRequest, await PassAsync(new { clientId = unreachableId, percent = 50m }));
        await using (var context = db.ContextFor(seed.TenantId)) Assert.Equal(parties, context.Parties.Count()); // nobody was made by any of it

        // A vet who is on a client list is a client too; a share can still go back to the house.
        Assert.Equal(HttpStatusCode.Created, await AddAsync(new { clientId = listedVetId, percent = 5m }));
        Assert.Equal(HttpStatusCode.NoContent, await PassAsync(new { clientId = houseId, percent = 50m }));
    }

    private sealed record ContactOnHorse(Guid Id, Guid PartyId, bool IsClient);

    private sealed record AddedClient(Guid Id, string Name, bool WasContact);

    [Fact]
    public async Task A_trainer_can_be_made_a_client_and_then_own_a_share()
    {
        var seed = await SeedAsync();
        var (terry, vera) = ($"terry-{Guid.NewGuid():N}@stable.test", $"vera-{Guid.NewGuid():N}@vets.test");
        Guid trainerId, vetId, houseId;
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var tenant = context.Tenants.Single();
            var (house, trainer, vet) = (new Party(tenant.Id, "Owners House", PartyKind.Organisation), new Party(tenant.Id, "Terry Trainer"), new Party(tenant.Id, "Vera Vet"));
            trainer.AddEmail(terry);
            vet.AddEmail(vera);
            context.Parties.AddRange(house, trainer, vet);
            tenant.SetHouseParty(house);
            var horse = context.Horses.Single(h => h.Id == seed.HorseId);
            context.HorseContacts.Add(HorseContact.Tag(horse, trainer, ContactRole.Trainer, [], DateTimeOffset.UtcNow)!);
            context.HorseContacts.Add(HorseContact.Tag(horse, vet, ContactRole.Vet, [], DateTimeOffset.UtcNow)!);
            await context.SaveChangesAsync();
            (trainerId, vetId, houseId) = (trainer.Id, vet.Id, house.Id);
        }

        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        var contacts = $"/api/horses/{seed.HorseId}/contacts";
        var line = (await manager.GetFromJsonAsync<List<ContactOnHorse>>(contacts))!.Single(c => c.PartyId == trainerId);
        Assert.False(line.IsClient);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/clients/{trainerId}")).StatusCode);

        // A viewer can't; staff can, and nothing is sent.
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"{contacts}/{line.Id}/client", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostApiAsync($"{contacts}/{Guid.NewGuid()}/client", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"{contacts}/{line.Id}/client", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"{contacts}/{line.Id}/client", new { })).StatusCode); // again: the same
        await DispatchAsync();
        Assert.DoesNotContain(Api.Emails.Sent, e => e.Email.ToAddress == terry);
        Assert.True((await manager.GetFromJsonAsync<List<ContactOnHorse>>(contacts))!.Single(c => c.PartyId == trainerId).IsClient); // still the trainer
        var client = (await manager.GetFromJsonAsync<ClientDetail>($"/api/clients/{trainerId}"))!;
        Assert.Equal(("Prospect", "Bel Esprit's trainer"), (client.Status, client.HowWeKnowThem));

        // Adding the vet on the Clients page, by their email, makes them a client under the name they have: nobody is doubled.
        var added = (await (await manager.PostApiAsync("/api/clients", new { name = "V. Vet", email = vera, mobile = (string?)null, howWeKnowThem = "Wants a share" }))
            .Content.ReadFromJsonAsync<AddedClient>())!;
        Assert.Equal(new AddedClient(vetId, "Vera Vet", true), added);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostApiAsync("/api/clients", new { name = "V. Vet", email = vera, mobile = (string?)null, howWeKnowThem = "Again" })).StatusCode);

        // Bob passes some to the trainer, some back to the house (which held nothing), and keeps the rest.
        var owners = $"/api/horses/{seed.HorseId}/owners";
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"{owners}/{seed.BobId}/transfer", new
        {
            recipients = new object[] { new { clientId = seed.BobId, percent = 30m }, new { clientId = trainerId, percent = 5m }, new { clientId = houseId, percent = 15m } },
            on = (string?)null, access = "LockedOut",
        })).StatusCode);
        var now = (await manager.GetFromJsonAsync<OwnerList>(owners))!;
        Assert.Equal([("Owners House", "15%"), ("Ann Owner", "50%"), ("Bob Owner", "30%"), ("Terry Trainer", "5%")], now.Owners.Select(o => (o.Name, o.Percent)));
        Assert.Equal(houseId, now.HouseId);
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == terry); // their first share: invited
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
        var danId = await ClientAsync(manager, "Dan Buyer", dan);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostApiAsync($"/api/horses/{horseId}/owners/{house.PartyId}/transfer", new
        {
            recipients = new object[] { new { clientId = house.PartyId, percent = 7.5m }, new { clientId = danId, percent = 2.5m } },
            on = (string?)null, access = "LockedOut",
        })).StatusCode);
        var owners = (await manager.GetFromJsonAsync<OwnerList>($"/api/horses/{horseId}/owners"))!;
        Assert.Equal([("Owners House", "7.5%"), ("Dan Buyer", "2.5%")], owners.Owners.Select(o => (o.Name, o.Percent)));
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == dan);
    }
}
