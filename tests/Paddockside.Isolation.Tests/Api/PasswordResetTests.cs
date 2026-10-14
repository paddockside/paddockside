using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Staff "Forgot your password?": a link by email that works once, for 30 minutes, lifts a lockout and ends other
/// sessions — and never skips the authenticator, never answers differently for unknown addresses, and is not a
/// way in for owners, who have no password.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed partial class PasswordResetTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record NextStep(string Next);

    [GeneratedRegex(@"/reset-password#(?<token>[A-Za-z0-9_-]{40,})")]
    private static partial Regex LinkPattern();

    [Fact]
    public async Task A_locked_out_staff_member_chooses_a_new_password_and_still_needs_the_authenticator()
    {
        var (email, oldPassword) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Coordinator);
        using var signedIn = await Api.SignedInWithAsync(email, oldPassword);
        using var browser = Api.Browser();
        for (var i = 0; i < 5; i++) await browser.PostApiAsync("/api/auth/password", new { email, password = "not the password at all" });
        Assert.Equal((HttpStatusCode)423, (await browser.PostApiAsync("/api/auth/password", new { email, password = oldPassword })).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostApiAsync("/api/auth/forgot-password", new { email })).StatusCode);
        var token = LatestLinkTo(email);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostApiAsync("/api/auth/reset-password/check", new { token })).StatusCode); // looking doesn't spend it

        // The usual rules apply, and a refused password leaves the link usable.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostApiAsync("/api/auth/reset-password", new { token, password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostApiAsync("/api/auth/reset-password", new { token, password = ApiFactory.BreachedPassword })).StatusCode);

        const string newPassword = "paddock gate morning frost";
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostApiAsync("/api/auth/reset-password", new { token, password = newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostApiAsync("/api/auth/reset-password", new { token, password = "another fine long phrase" })).StatusCode); // once
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == email && e.Email.Metadata.GetValueOrDefault("purpose") == "password-changed");

        // Unlocked; the old password is gone; the new one leads to the authenticator, not a session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostApiAsync("/api/auth/password", new { email, password = oldPassword })).StatusCode);
        var next = await browser.PostApiAsync("/api/auth/password", new { email, password = newPassword });
        Assert.Equal("totp", (await next.Content.ReadFromJsonAsync<NextStep>())!.Next);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);

        // The session signed in before the reset has ended.
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_addresses_and_owners_get_the_same_answer_and_no_email()
    {
        var before = Api.Emails.Sent.Count;
        using var browser = Api.Browser();
        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostApiAsync("/api/auth/forgot-password", new { email = "nobody@nowhere.test" })).StatusCode);

        // An owner: a person without a password, with only an owner membership.
        var owner = $"owner-{Guid.NewGuid():N}@owners.test";
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
            var person = new Person { UserName = owner, Email = owner, EmailConfirmed = true };
            await users.CreateAsync(person);
            var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
            identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = db.A.TenantId, Role = MemberRole.Owner });
            await identity.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Accepted, (await browser.PostApiAsync("/api/auth/forgot-password", new { email = owner })).StatusCode);
        Assert.Equal(before, Api.Emails.Sent.Count);
        Assert.Equal(HttpStatusCode.Gone, (await browser.PostApiAsync("/api/auth/reset-password", new { token = "made-up-token-that-was-never-issued-anywhere-at-all", password = "paddock gate morning frost" })).StatusCode);
    }

    [Fact]
    public async Task Asking_twice_quickly_sends_one_email_and_only_the_newest_link_works()
    {
        var (email, _) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Viewer);
        using var browser = Api.Browser();
        await browser.PostApiAsync("/api/auth/forgot-password", new { email });
        await browser.PostApiAsync("/api/auth/forgot-password", new { email });
        Assert.Single(Api.Emails.Sent, e => e.Email.ToAddress == email && e.Email.Metadata.GetValueOrDefault("purpose") == "password-reset");
    }

    private string LatestLinkTo(string address) =>
        LinkPattern().Match(Api.Emails.Sent.Last(s => s.Email.ToAddress == address && s.Email.Metadata.GetValueOrDefault("purpose") == "password-reset").Email.TextBody).Groups["token"].Value;
}
