using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Every horse gets its own inbox address automatically, from its sale lot and each registered name
/// (messaging-channels.md §3.1), and staff see it on the horse page.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class HorseAddressTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record HorseDetail(Guid Id, string Name, string? InboxAddress);

    [Fact]
    public async Task Saving_a_horse_issues_its_address_and_a_rename_keeps_the_old_one()
    {
        var (tenant, slug) = await NewTenantAsync();
        var horse = new Horse(tenant, "Lot 231", HorseNameKind.SaleLot, DateTimeOffset.UtcNow.AddYears(-1));
        horse.AddName("Pearl Diver", HorseNameKind.StableName, DateTimeOffset.UtcNow.AddMonths(-11));
        await using (var context = db.ContextFor(tenant))
        {
            context.Add(horse);
            await context.SaveChangesAsync();
        }

        Assert.Equal(["lot-231"], await AddressesAsync(tenant, horse.Id)); // a stable name is a nickname: no address

        await using (var context = db.ContextFor(tenant))
        {
            var named = await context.Horses.SingleAsync(h => h.Id == horse.Id);
            named.AddName("Ocean Pearl", HorseNameKind.Registered, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        Assert.Equal(["lot-231", "ocean-pearl"], await AddressesAsync(tenant, horse.Id));

        using var staff = await Api.SignedInAsync(tenant, MemberRole.Coordinator);
        var page = await staff.GetFromJsonAsync<HorseDetail>($"/api/horses/{horse.Id}");
        Assert.Equal($"ocean-pearl@{slug}.in.paddockside.com.au", page!.InboxAddress); // the current name's
    }

    [Fact]
    public async Task Two_horses_with_the_same_name_both_get_an_address()
    {
        var (tenant, _) = await NewTenantAsync();
        var first = new Horse(tenant, "Bel Esprit", HorseNameKind.Registered, DateTimeOffset.UtcNow.AddYears(-8));
        var second = new Horse(tenant, "Bel Esprit", HorseNameKind.Registered, DateTimeOffset.UtcNow);
        await using (var context = db.ContextFor(tenant))
        {
            context.AddRange(first, second);
            await context.SaveChangesAsync();
        }

        Assert.Equal(["bel-esprit"], await AddressesAsync(tenant, first.Id));
        Assert.Equal(["bel-esprit-2"], await AddressesAsync(tenant, second.Id));
    }

    private async Task<List<string>> AddressesAsync(Guid tenant, Guid horseId)
    {
        await using var context = db.ContextFor(tenant);
        return await context.RoutingAddresses
            .Where(r => r.Kind == RoutingAddressKind.HorseInbox && r.HorseId == horseId)
            .OrderBy(r => r.Token)
            .Select(r => r.Token)
            .ToListAsync();
    }

    private async Task<(Guid TenantId, string Slug)> NewTenantAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Addresses {suffix}", $"ad{suffix}");
        await using var context = db.ContextFor(tenant.Id);
        context.Add(tenant);
        await context.SaveChangesAsync();
        return (tenant.Id, tenant.Slug);
    }
}
