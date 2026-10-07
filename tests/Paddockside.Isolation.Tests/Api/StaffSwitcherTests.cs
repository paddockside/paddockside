using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Staff in more than one tenant (identity-access.md §3): one person, a switcher, the active tenant in the session
/// (never the URL). The test server rechecks sessions on every request, so these also prove a switch survives the
/// check, and that losing the membership switched into falls back safely.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class StaffSwitcherTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record Option(Guid Id, string Name, string Role, bool Current);

    private sealed record Me(string Email, string TenantName, string Role);

    private sealed record Horse(Guid Id, string Name);

    private sealed record Enrolment(string AuthenticatorUri);

    [Fact]
    public async Task A_person_switches_between_their_tenants_and_the_switch_holds()
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Viewer);
        var (otherTenant, membershipId) = await JoinNewTenantAsync(email, MemberRole.Coordinator);
        using var browser = await SignInAsync(email, password);

        // Signed in to the first tenant they joined.
        Assert.Equal("Tenant A", (await browser.GetFromJsonAsync<Me>("/api/auth/me"))!.TenantName);
        var options = await browser.GetFromJsonAsync<List<Option>>("/api/auth/tenants");
        Assert.Equal(2, options!.Count);
        Assert.True(options.Single(o => o.Id == db.A.TenantId).Current);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostApiAsync("/api/auth/switch", new { tenantId = otherTenant })).StatusCode);

        // Several requests, each rechecking the session: still in the tenant they chose, with that tenant's role.
        for (var i = 0; i < 3; i++)
        {
            var me = await browser.GetFromJsonAsync<Me>("/api/auth/me");
            Assert.Equal(("Switch target", "Coordinator"), (me!.TenantName[..13], me.Role));
        }

        Assert.Empty((await browser.GetFromJsonAsync<List<Horse>>("/api/horses"))!); // the other tenant's horses: none

        // Losing that membership drops them back to the one they still hold, at the next request.
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
            (await identity.Memberships.SingleAsync(m => m.Id == membershipId)).Status = MembershipStatus.Suspended;
            await identity.SaveChangesAsync();
        }

        Assert.Equal("Tenant A", (await browser.GetFromJsonAsync<Me>("/api/auth/me"))!.TenantName);
    }

    [Fact]
    public async Task Nobody_switches_into_a_tenant_they_are_not_staff_in()
    {
        using var browser = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostApiAsync("/api/auth/switch", new { tenantId = db.B.TenantId })).StatusCode);
        Assert.Equal("Tenant A", (await browser.GetFromJsonAsync<Me>("/api/auth/me"))!.TenantName);
    }

    /// <summary>A new tenant, with this person added to its staff (as if they had accepted an invitation).</summary>
    private async Task<(Guid TenantId, Guid MembershipId)> JoinNewTenantAsync(string email, MemberRole role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Switch target {suffix}", $"sw{suffix}");
        await using (var context = db.ContextFor(tenant.Id))
        {
            context.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using var scope = Api.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
        var person = await identity.Users.SingleAsync(p => p.Email == email);
        var membership = new Membership { PersonId = person.Id, TenantId = tenant.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        identity.Memberships.Add(membership);
        await identity.SaveChangesAsync();
        return (tenant.Id, membership.Id);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var browser = Api.Browser();
        (await browser.PostApiAsync("/api/auth/password", new { email, password })).EnsureSuccessStatusCode();
        var enrolment = await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        (await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();
        return browser;
    }
}
