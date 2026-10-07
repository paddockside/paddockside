using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Staff membership (identity-access.md §5.2, §6): tenant admins invite by email with a role; the invited person
/// chooses or proves a password and then sets up their second factor; admins change roles and suspend; the last
/// tenant admin is protected; and nobody manages another tenant's members.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed partial class MemberTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record Member(Guid Id, string Email, string Role, string Status, bool IsYou);

    private sealed record Invitation(Guid Id, string Email, string Role);

    private sealed record MembersPage(List<Member> Members, List<Invitation> Invitations);

    private sealed record JoinDetails(string TenantName, string Email, string Role, string Needs);

    private sealed record NextStep(string Next);

    private sealed record Me(string Email, string TenantName, string Role);

    private sealed record Enrolment(string AuthenticatorUri);

    [GeneratedRegex(@"/join#(?<token>[A-Za-z0-9_-]{40,})")]
    private static partial Regex JoinLink();

    [Fact]
    public async Task An_invited_person_chooses_a_password_sets_up_an_authenticator_and_is_staff()
    {
        var (tenantId, admin) = await NewTenantWithAdminAsync();
        var address = $"new-{Guid.NewGuid():N}@laurel-oak.test";

        var invited = await admin.PostApiAsync("/api/members/invitations", new { email = address, role = "Coordinator" });
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var token = TokenSentTo(address);
        var invitation = Api.Emails.Sent.Last(e => e.Email.ToAddress == address).Email;
        Assert.False(invitation.TrackOpens);
        Assert.DoesNotContain("password", invitation.TextBody, StringComparison.OrdinalIgnoreCase); // login-bait wording stays on the page

        using var browser = Api.Browser();
        var details = await (await browser.PostApiAsync("/api/join/inspect", new { token })).Content.ReadFromJsonAsync<JoinDetails>();
        Assert.Equal(("Coordinator", "new-password", address), (details!.Role, details.Needs, details.Email));

        var accepted = await browser.PostApiAsync("/api/join/accept", new { token, password = "a long new staff password" });
        Assert.Equal("enrol", (await accepted.Content.ReadFromJsonAsync<NextStep>())!.Next);
        var enrolment = await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        (await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();

        var me = await browser.GetFromJsonAsync<Me>("/api/auth/me");
        Assert.Equal((address, "Coordinator"), (me!.Email, me.Role));
        var page = await admin.GetFromJsonAsync<MembersPage>("/api/members");
        Assert.Contains(page!.Members, m => m.Email == address && m.Role == "Coordinator" && m.Status == "Active");
        Assert.DoesNotContain(page.Invitations, i => i.Email == address);

        // Single use.
        using var again = Api.Browser();
        Assert.Equal(HttpStatusCode.Gone, (await again.PostApiAsync("/api/join/accept", new { token, password = "a long new staff password" })).StatusCode);
    }

    [Fact]
    public async Task Only_a_tenant_admin_manages_members_and_only_their_own_tenant()
    {
        var (tenantId, admin) = await NewTenantWithAdminAsync();
        using var coordinator = await Api.SignedInAsync(tenantId, MemberRole.Coordinator);

        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostApiAsync("/api/members/invitations", new { email = "x@y.test", role = "TenantAdmin" })).StatusCode);

        // Tenant A's admin sees only tenant A's members, and cannot touch this tenant's.
        using var otherAdmin = await Api.SignedInAsync(db.A.TenantId, MemberRole.TenantAdmin);
        var mine = await admin.GetFromJsonAsync<MembersPage>("/api/members");
        var theirs = await otherAdmin.GetFromJsonAsync<MembersPage>("/api/members");
        Assert.Empty(mine!.Members.Select(m => m.Id).Intersect(theirs!.Members.Select(m => m.Id)));
        var target = mine.Members.First(m => !m.IsYou);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.PostApiAsync($"/api/members/{target.Id}/suspend", new { })).StatusCode);
    }

    [Fact]
    public async Task A_cancelled_or_replaced_invitation_no_longer_works()
    {
        var (_, admin) = await NewTenantWithAdminAsync();
        var address = $"twice-{Guid.NewGuid():N}@laurel-oak.test";

        await admin.PostApiAsync("/api/members/invitations", new { email = address, role = "Viewer" });
        var first = TokenSentTo(address);
        await admin.PostApiAsync("/api/members/invitations", new { email = address, role = "Manager" }); // replaces the first
        var second = TokenSentTo(address);

        using var browser = Api.Browser();
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostApiAsync("/api/join/inspect", new { token = first })).StatusCode);
        var invitation = (await admin.GetFromJsonAsync<MembersPage>("/api/members"))!.Invitations.Single(i => i.Email == address);
        Assert.Equal("Manager", invitation.Role);

        await admin.PostApiAsync($"/api/members/invitations/{invitation.Id}/revoke", new { });
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostApiAsync("/api/join/inspect", new { token = second })).StatusCode);
    }

    [Fact]
    public async Task A_breached_password_is_refused_when_joining()
    {
        var (_, admin) = await NewTenantWithAdminAsync();
        var address = $"weak-{Guid.NewGuid():N}@laurel-oak.test";
        await admin.PostApiAsync("/api/members/invitations", new { email = address, role = "Viewer" });

        using var browser = Api.Browser();
        var refused = await browser.PostApiAsync("/api/join/accept", new { token = TokenSentTo(address), password = ApiFactory.BreachedPassword });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task An_existing_person_proves_their_password_and_gains_the_membership()
    {
        var (tenantId, admin) = await NewTenantWithAdminAsync();
        var (address, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Viewer); // staff elsewhere already

        await admin.PostApiAsync("/api/members/invitations", new { email = address, role = "Coordinator" });
        var token = TokenSentTo(address);
        using var browser = Api.Browser();
        Assert.Equal("existing-password", (await (await browser.PostApiAsync("/api/join/inspect", new { token })).Content.ReadFromJsonAsync<JoinDetails>())!.Needs);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostApiAsync("/api/join/accept", new { token, password = "not their password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostApiAsync("/api/join/accept", new { token, password })).StatusCode);

        await using var scope = Api.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
        var person = await identity.Users.SingleAsync(p => p.Email == address);
        Assert.True(await identity.Memberships.AnyAsync(m => m.PersonId == person.Id && m.TenantId == tenantId && m.Role == MemberRole.Coordinator));
        Assert.True(await identity.Memberships.AnyAsync(m => m.PersonId == person.Id && m.TenantId == db.A.TenantId)); // the other one stays
    }

    [Fact]
    public async Task The_last_tenant_admin_cannot_be_demoted_or_suspended_and_nobody_suspends_themselves()
    {
        var (tenantId, admin) = await NewTenantWithAdminAsync();
        var page = await admin.GetFromJsonAsync<MembersPage>("/api/members");
        var me = page!.Members.Single(m => m.IsYou);
        var coordinator = page.Members.Single(m => !m.IsYou);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostApiAsync($"/api/members/{me.Id}/suspend", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostApiAsync($"/api/members/{me.Id}/role", new { role = "Viewer" })).StatusCode);

        // With a second admin, the first can be demoted — by the second.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/members/{coordinator.Id}/role", new { role = "TenantAdmin" })).StatusCode);
        using var second = await SignInExistingAsync(coordinator.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostApiAsync($"/api/members/{me.Id}/role", new { role = "Manager" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostApiAsync($"/api/members/{coordinator.Id}/role", new { role = "Viewer" })).StatusCode); // now they are the last
    }

    [Fact]
    public async Task A_suspended_member_cannot_sign_in_until_reactivated()
    {
        var (tenantId, admin) = await NewTenantWithAdminAsync();
        var page = await admin.GetFromJsonAsync<MembersPage>("/api/members");
        var coordinator = page!.Members.Single(m => !m.IsYou);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/members/{coordinator.Id}/suspend", new { })).StatusCode);
        using var browser = Api.Browser();
        var refused = await browser.PostApiAsync("/api/auth/password", new { email = coordinator.Email, password = Passwords[coordinator.Email] });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/members/{coordinator.Id}/reactivate", new { })).StatusCode);
        var allowed = await browser.PostApiAsync("/api/auth/password", new { email = coordinator.Email, password = Passwords[coordinator.Email] });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private readonly Dictionary<string, string> Passwords = [];

    private string TokenSentTo(string address) =>
        JoinLink().Match(Api.Emails.Sent.Last(s => s.Email.ToAddress == address && s.Email.Metadata.GetValueOrDefault("purpose") == "staff-invitation").Email.TextBody).Groups["token"].Value;

    /// <summary>A tenant of its own with a signed-in tenant admin and one coordinator.</summary>
    private async Task<(Guid TenantId, HttpClient Admin)> NewTenantWithAdminAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Members {suffix}", $"mb{suffix}");
        await using (var context = db.ContextFor(tenant.Id))
        {
            context.Add(tenant);
            await context.SaveChangesAsync();
        }

        var (coordinatorEmail, coordinatorPassword) = await Api.CreatePersonAsync(tenant.Id, MemberRole.Coordinator);
        Passwords[coordinatorEmail] = coordinatorPassword;
        return (tenant.Id, await Api.SignedInAsync(tenant.Id, MemberRole.TenantAdmin));
    }

    /// <summary>Signs in a person created by <see cref="NewTenantWithAdminAsync"/>, enrolling their authenticator.</summary>
    private async Task<HttpClient> SignInExistingAsync(string email)
    {
        var browser = Api.Browser();
        await browser.PostApiAsync("/api/auth/password", new { email, password = Passwords[email] });
        var enrolment = await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        (await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();
        return browser;
    }
}
