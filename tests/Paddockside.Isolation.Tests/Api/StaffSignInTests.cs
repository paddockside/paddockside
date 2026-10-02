using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Who can get in (identity-access.md §4.2) and what the horses endpoint shows them (non-functional.md §2).
/// Runs the real Api against the isolation database.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class StaffSignInTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record NextStep(string Next);

    private sealed record Enrolment(string SharedKey, string AuthenticatorUri, string QrCodePng);

    private sealed record RecoveryCodes(List<string> Codes);

    private sealed record Horse(Guid Id, string Name);

    [Fact]
    public async Task Horses_need_a_signed_in_session()
    {
        using var browser = Api.Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_password_alone_never_opens_a_session()
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Coordinator);
        using var browser = Api.Browser();

        var step = await browser.PostApiAsync("/api/auth/password", new { email, password });

        Assert.Equal("enrol", (await step.Content.ReadFromJsonAsync<NextStep>())!.Next);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/horses")).StatusCode);
    }

    [Fact]
    public async Task First_sign_in_enrols_an_authenticator_then_shows_only_the_tenants_own_horses()
    {
        foreach (var (tenant, other) in new[] { (db.A, db.B), (db.B, db.A) })
        {
            var (email, password) = await Api.CreatePersonAsync(tenant.TenantId, MemberRole.Viewer);
            using var browser = Api.Browser();
            await browser.PostApiAsync("/api/auth/password", new { email, password });

            var enrolment = await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
            Assert.NotEmpty(enrolment!.QrCodePng);
            var completed = await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment.AuthenticatorUri)) });

            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal(10, (await completed.Content.ReadFromJsonAsync<RecoveryCodes>())!.Codes.Count);

            var horses = await browser.GetFromJsonAsync<List<Horse>>("/api/horses");
            Assert.Equal(tenant.HorseId, Assert.Single(horses!).Id);
            Assert.DoesNotContain(horses!, h => h.Id == other.HorseId);
        }
    }

    [Fact]
    public async Task Enrolled_staff_sign_in_with_a_code_and_their_key_is_never_shown_again()
    {
        var (email, password, key, _) = await EnrolAsync(db.A.TenantId);
        using var browser = Api.Browser();

        var step = await browser.PostApiAsync("/api/auth/password", new { email, password });
        Assert.Equal("totp", (await step.Content.ReadFromJsonAsync<NextStep>())!.Next);

        // Someone holding only the password cannot re-read the authenticator key.
        Assert.Equal(HttpStatusCode.Conflict, (await browser.GetAsync("/api/auth/enrolment")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostApiAsync("/api/auth/totp", new { code = WrongCode(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/horses")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostApiAsync("/api/auth/totp", new { code = Totp.Code(key) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/horses")).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var (email, password, _, codes) = await EnrolAsync(db.A.TenantId);

        using (var first = Api.Browser())
        {
            await first.PostApiAsync("/api/auth/password", new { email, password });
            Assert.Equal(HttpStatusCode.NoContent, (await first.PostApiAsync("/api/auth/recovery-code", new { code = codes[0] })).StatusCode);
        }

        using var second = Api.Browser();
        await second.PostApiAsync("/api/auth/password", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostApiAsync("/api/auth/recovery-code", new { code = codes[0] })).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_answer()
    {
        var (email, _) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Coordinator);
        using var browser = Api.Browser();

        var wrongPassword = await browser.PostApiAsync("/api/auth/password", new { email, password = "not the password at all" });
        var unknownEmail = await browser.PostApiAsync("/api/auth/password", new { email = "nobody@tenant.test", password = "not the password at all" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownEmail.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Owners_cannot_use_the_staff_password_sign_in()
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Owner);
        using var browser = Api.Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostApiAsync("/api/auth/password", new { email, password })).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Coordinator);
        using var browser = Api.Browser();

        for (var attempt = 0; attempt < 5; attempt++)
            await browser.PostApiAsync("/api/auth/password", new { email, password = $"wrong guess number {attempt}" });

        Assert.Equal(HttpStatusCode.Locked, (await browser.PostApiAsync("/api/auth/password", new { email, password })).StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_without_the_api_header_are_refused()
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, MemberRole.Coordinator);
        using var browser = Api.Browser();

        var response = await browser.PostAsJsonAsync("/api/auth/password", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("eleven-char")]
    [InlineData(ApiFactory.BreachedPassword)]
    public async Task Short_or_breached_passwords_are_refused(string password)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
        var email = $"new-{Guid.NewGuid():N}@tenant.test";

        var result = await users.CreateAsync(new Person { UserName = email, Email = email }, password);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_long_password_with_no_composition_rules_is_accepted()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
        var email = $"new-{Guid.NewGuid():N}@tenant.test";

        var result = await users.CreateAsync(new Person { UserName = email, Email = email }, "paddocks and silks at dawn");

        Assert.True(result.Succeeded);
    }

    /// <summary>A staff member who has completed enrolment; returns their key and recovery codes.</summary>
    private async Task<(string Email, string Password, string Key, List<string> Codes)> EnrolAsync(Guid tenantId)
    {
        var (email, password) = await Api.CreatePersonAsync(tenantId, MemberRole.Coordinator);
        using var browser = Api.Browser();
        await browser.PostApiAsync("/api/auth/password", new { email, password });
        var enrolment = await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        var key = Totp.SecretFrom(enrolment!.AuthenticatorUri);
        var completed = await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(key) });
        return (email, password, key, (await completed.Content.ReadFromJsonAsync<RecoveryCodes>())!.Codes);
    }

    /// <summary>A code from far enough away in time that no validation window accepts it.</summary>
    private static string WrongCode(string key) => Totp.Code(key, DateTimeOffset.UtcNow.AddHours(-1));
}

/// <summary>One Api host per test class, built on first use over the shared isolation database.</summary>
public sealed class ApiFactoryFixture : IDisposable
{
    private ApiFactory? _factory;

    public ApiFactory For(IsolationDatabase db) => _factory ??= new ApiFactory(db);

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
