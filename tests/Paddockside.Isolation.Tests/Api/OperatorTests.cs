using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The operator console (identity-access.md §7): operators see every tenant's account and health and onboard new
/// tenants, but never what is inside a tenant; nobody else reaches the console; operators are made by invitation
/// (or the command line), never by a tenant.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed partial class OperatorTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record TenantRow(Guid Id, string Name, string Slug, int Horses, int Staff, int EmailsSent);

    private sealed record TenantCreated(Guid Id, string Slug, bool InvitationSent);

    private sealed record NextStep(string Next);

    private sealed record Enrolment(string AuthenticatorUri);

    [GeneratedRegex(@"/join#(?<token>[A-Za-z0-9_-]{40,})")]
    private static partial Regex JoinLink();

    [Fact]
    public async Task An_operator_sees_every_tenants_account_and_none_of_its_content()
    {
        var (ops, _) = await Api.SignedInOperatorAsync();

        var response = await ops.GetAsync("/api/ops/tenants");
        var raw = await response.Content.ReadAsStringAsync();
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<TenantRow>>(raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var a = rows.Single(r => r.Id == db.A.TenantId);
        Assert.Equal("Tenant A", a.Name);
        Assert.Equal(1, a.Horses);
        Assert.Contains(rows, r => r.Id == db.B.TenantId);
        // Counts, never content: no horse, owner or message text anywhere in the answer.
        Assert.DoesNotContain("Tenant A Mare", raw);
        Assert.DoesNotContain("Tenant A Ann", raw);
        Assert.DoesNotContain("Tenant A update", raw);

        // And no route into a tenant's data: an operator has no tenant.
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/horses")).StatusCode);
    }

    [Fact]
    public async Task Staff_and_owners_cannot_reach_the_console()
    {
        using var admin = await Api.SignedInAsync(db.A.TenantId, MemberRole.TenantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/ops/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostApiAsync("/api/ops/tenants", new { name = "Sneaky", slug = "sneaky", adminEmail = "x@y.test" })).StatusCode);

        using var anonymous = Api.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ops/tenants")).StatusCode);
    }

    [Fact]
    public async Task Onboarding_creates_the_tenant_and_its_first_admin_joins_it()
    {
        var (ops, _) = await Api.SignedInOperatorAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var adminEmail = $"principal-{suffix}@stud.test";

        var created = await ops.PostApiAsync("/api/ops/tenants", new { name = $"Riverbend Stud {suffix}", slug = $"rb{suffix}", adminEmail });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = (await created.Content.ReadFromJsonAsync<TenantCreated>())!;
        Assert.Equal($"rb{suffix}", tenant.Slug);

        // The short name is now taken.
        Assert.Equal(HttpStatusCode.Conflict, (await ops.PostApiAsync("/api/ops/tenants", new { name = "Another", slug = $"rb{suffix}", adminEmail })).StatusCode);

        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == adminEmail).Email;
        Assert.Equal($"Riverbend Stud {suffix} via Paddockside", email.FromName);
        var token = JoinLink().Match(email.TextBody).Groups["token"].Value;

        using var principal = Api.Browser();
        Assert.Equal("enrol", (await (await principal.PostApiAsync("/api/join/accept", new { token, password = "a long new principal password" })).Content.ReadFromJsonAsync<NextStep>())!.Next);
        var enrolment = await principal.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        (await principal.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();

        var members = await principal.GetStringAsync("/api/members"); // a tenant admin of the new tenant
        Assert.Contains(adminEmail, members);
    }

    [Fact]
    public async Task An_invited_operator_joins_with_no_tenant_and_reaches_the_console()
    {
        var (ops, _) = await Api.SignedInOperatorAsync();
        var address = $"new-operator-{Guid.NewGuid():N}@paddockside.test";

        Assert.Equal(HttpStatusCode.Created, (await ops.PostApiAsync("/api/ops/operators/invitations", new { email = address })).StatusCode);
        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == address).Email;
        Assert.Equal("operator-invitation", email.Metadata["purpose"]);

        using var newcomer = Api.Browser();
        await newcomer.PostApiAsync("/api/join/accept", new { token = JoinLink().Match(email.TextBody).Groups["token"].Value, password = "a long new operator password" });
        var enrolment = await newcomer.GetFromJsonAsync<Enrolment>("/api/auth/enrolment");
        (await newcomer.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await newcomer.GetAsync("/api/ops/tenants")).StatusCode);
        await using var scope = Api.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
        var person = await identity.Users.SingleAsync(p => p.Email == address);
        Assert.True(person.IsOperator);
        Assert.False(await identity.Memberships.AnyAsync(m => m.PersonId == person.Id)); // the product, not a tenant
    }
}
