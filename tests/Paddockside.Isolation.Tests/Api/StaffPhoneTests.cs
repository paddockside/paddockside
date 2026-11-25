using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// A member of staff's own phone stays signed in for a week (David, 25 Nov 2026), and can be signed out from anywhere. The
/// test server rechecks sessions on every request, so a phone signed out is out at once here (a minute, really).
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class StaffPhoneTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record Enrolment(string AuthenticatorUri);

    private sealed record Phone(Guid Id, string Label, string LastUsed, bool ThisOne);

    private sealed record Member(string Email, List<Phone> Phones);

    private sealed record MembersPage(List<Member> Members);

    private sealed record Me(string Email, bool IsOperator);

    private async Task<(string Email, string Password, string Key)> EnrolledAsync(MemberRole role)
    {
        var (email, password) = await Api.CreatePersonAsync(db.A.TenantId, role);
        using var browser = Api.Browser();
        await browser.PostApiAsync("/api/auth/password", new { email, password });
        var key = Totp.SecretFrom((await browser.GetFromJsonAsync<Enrolment>("/api/auth/enrolment"))!.AuthenticatorUri);
        await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(key) });
        return (email, password, key);
    }

    private async Task<(HttpClient Browser, HttpResponseMessage Response)> SignInAsync((string Email, string Password, string Key) who, bool keep)
    {
        var browser = Api.Browser();
        browser.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X)");
        await browser.PostApiAsync("/api/auth/password", new { email = who.Email, password = who.Password });
        return (browser, await browser.PostApiAsync("/api/auth/totp", new { code = Totp.Code(who.Key), keepSignedIn = keep }));
    }

    private static DateTimeOffset Expiry(HttpResponseMessage response) =>
        DateTimeOffset.Parse(System.Text.RegularExpressions.Regex.Match(response.Headers.GetValues("Set-Cookie").Last(c => c.StartsWith("__Host-paddockside=", StringComparison.Ordinal)), "expires=([^;]+)").Groups[1].Value);

    [Fact]
    public async Task Their_own_phone_stays_signed_in_for_a_week_and_is_listed_and_signed_out_by_them()
    {
        var who = await EnrolledAsync(MemberRole.Coordinator);
        var (computer, ordinary) = await SignInAsync(who, keep: false);
        Assert.InRange(Expiry(ordinary) - DateTimeOffset.UtcNow, TimeSpan.FromHours(11.5), TimeSpan.FromHours(12.5));
        Assert.Empty((await computer.GetFromJsonAsync<List<Phone>>("/api/account/phones"))!);

        var (phone, kept) = await SignInAsync(who, keep: true);
        Assert.InRange(Expiry(kept) - DateTimeOffset.UtcNow, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/horses")).StatusCode); // survives the rechecks
        Assert.True(Assert.Single((await phone.GetFromJsonAsync<List<Phone>>("/api/account/phones"))!).ThisOne);
        var listed = Assert.Single((await computer.GetFromJsonAsync<List<Phone>>("/api/account/phones"))!);
        Assert.Equal(("iPhone", false), (listed.Label, listed.ThisOne));

        // Lost: signed out from the computer, which stays signed in. Someone else can't.
        using var stranger = await Api.SignedInAsync(db.A.TenantId, MemberRole.Manager);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostApiAsync($"/api/account/phones/{listed.Id}/sign-out", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostApiAsync($"/api/members/phones/{listed.Id}/sign-out", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await computer.PostApiAsync($"/api/account/phones/{listed.Id}/sign-out", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await computer.GetAsync("/api/horses")).StatusCode);
        Assert.Empty((await computer.GetFromJsonAsync<List<Phone>>("/api/account/phones"))!);
    }

    [Fact]
    public async Task An_admin_signs_out_their_staffs_phones_but_not_another_businesss()
    {
        var who = await EnrolledAsync(MemberRole.Coordinator);
        var (phone, _) = await SignInAsync(who, keep: true);
        using var admin = await Api.SignedInAsync(db.A.TenantId, MemberRole.TenantAdmin);
        using var elsewhere = await Api.SignedInAsync(db.B.TenantId, MemberRole.TenantAdmin);
        var device = Assert.Single((await admin.GetFromJsonAsync<MembersPage>("/api/members"))!.Members.Single(m => m.Email == who.Email).Phones);
        Assert.DoesNotContain((await elsewhere.GetFromJsonAsync<MembersPage>("/api/members"))!.Members, m => m.Phones.Any(p => p.Id == device.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.PostApiAsync($"/api/members/phones/{device.Id}/sign-out", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/members/phones/{device.Id}/sign-out", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/horses")).StatusCode);
        await using var context = db.ContextFor(db.A.TenantId);
        Assert.Contains(await context.AuditEntries.Where(a => a.Action == "auth.phone-signed-out").Select(a => a.Summary).ToListAsync(), s => s.Contains(who.Email));
    }

    [Fact]
    public async Task Signing_out_on_the_phone_ends_it_and_switching_business_keeps_it()
    {
        var who = await EnrolledAsync(MemberRole.Coordinator);
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
            var person = await identity.Users.SingleAsync(p => p.Email == who.Email);
            identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = db.B.TenantId, Role = MemberRole.Viewer, Status = MembershipStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
            await identity.SaveChangesAsync();
        }

        var (phone, _) = await SignInAsync(who, keep: true);
        var switched = await phone.PostApiAsync("/api/auth/switch", new { tenantId = db.B.TenantId });
        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        Assert.InRange(Expiry(switched) - DateTimeOffset.UtcNow, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));
        var device = Assert.Single((await phone.GetFromJsonAsync<List<Phone>>("/api/account/phones"))!);
        Assert.True(device.ThisOne);

        await phone.PostApiAsync("/api/auth/sign-out", new { });
        await using (var scope = Api.Services.CreateAsyncScope())
            Assert.NotNull((await scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>().StaffDevices.SingleAsync(d => d.Id == device.Id)).RevokedAt);
    }

    [Fact]
    public async Task The_operator_console_is_never_open_to_a_kept_phone()
    {
        var who = await EnrolledAsync(MemberRole.Manager);
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
            var person = (await users.FindByEmailAsync(who.Email))!;
            person.IsOperator = true;
            await users.UpdateAsync(person);
        }

        var (computer, _) = await SignInAsync(who, keep: false);
        Assert.Equal(HttpStatusCode.OK, (await computer.GetAsync("/api/ops/me")).StatusCode);
        var (phone, _) = await SignInAsync(who, keep: true);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync("/api/ops/me")).StatusCode); // after a recheck too
        Assert.False((await phone.GetFromJsonAsync<Me>("/api/auth/me"))!.IsOperator);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/horses")).StatusCode);
    }

    [Fact]
    public void Never_past_thirty_days_from_signing_in_or_a_week_unused()
    {
        var now = DateTimeOffset.UtcNow;
        var absolute = TimeSpan.FromDays(30);
        Assert.True(new StaffDevice { CreatedAt = now.AddDays(-29), LastSeenAt = now }.IsLive(now, absolute));
        Assert.False(new StaffDevice { CreatedAt = now.AddDays(-31), LastSeenAt = now }.IsLive(now, absolute));
        Assert.False(new StaffDevice { CreatedAt = now.AddDays(-10), LastSeenAt = now.AddDays(-8) }.IsLive(now, absolute));
        Assert.False(new StaffDevice { CreatedAt = now, LastSeenAt = now, RevokedAt = now }.IsLive(now, absolute));
    }
}
