using System.Net;
using System.Text.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>Which build is being served (David, 26 Nov 2026), for open tabs to see there's a newer one.</summary>
public sealed partial class OwnerSignInTests
{
    [Fact]
    public async Task The_build_being_served_can_be_asked_for_without_a_sign_in_or_the_database()
    {
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var response = await anyone.GetAsync("/version.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var served = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Paddockside.Web.BuildInfo.Number, served.GetProperty("number").GetString());
        Assert.Equal(Paddockside.Web.BuildInfo.Commit, served.GetProperty("commit").GetString());
        Assert.Equal(["number", "commit"], served.EnumerateObject().Select(p => p.Name));

        // Here a session is checked, and its cookie renewed, on every request that looks at it: this one doesn't.
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        Assert.True((await staff.GetAsync("/api/auth/me")).Headers.Contains("Set-Cookie"));
        Assert.False((await staff.GetAsync("/version.json")).Headers.Contains("Set-Cookie"));
    }
}
