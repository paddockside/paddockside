using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Support sessions (identity-access.md §7) and the audit log (§8): an operator gets into a tenant only with an
/// admin's approval, only read-only, only until it ends — and everything they look at, like every sign-in and
/// membership change, is in the tenant's append-only log.
/// </summary>
[Collection(IsolationCollection.Name)]
public sealed class SupportAndAuditTests(IsolationDatabase db, ApiFactoryFixture api) : IClassFixture<ApiFactoryFixture>
{
    private ApiFactory Api => api.For(db);

    private sealed record SupportView(Guid Id, Guid TenantId, string Status);

    private sealed record AuditView(string Who, string ActorKind, string Action, string Summary, string? Before, string? After);

    private sealed record AuditPage(List<AuditView> Entries, string? Older);

    private sealed record Member(Guid Id, string Email, bool IsYou);

    private sealed record MembersPage(List<Member> Members);

    [Fact]
    public async Task An_operator_gets_in_only_with_approval_only_read_only_and_only_until_it_ends()
    {
        var (tenantId, admin) = await NewTenantAsync();
        var (ops, _) = await Api.SignedInOperatorAsync();

        var requested = await ops.PostApiAsync($"/api/ops/tenants/{tenantId}/support", new { reason = "Owner says the tickets prompt is missing", hours = 2 });
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
        var session = (await requested.Content.ReadFromJsonAsync<SupportView>())!;
        Assert.Equal("Requested", session.Status);
        Assert.Contains(Api.Emails.Sent, e => e.Email.Metadata.GetValueOrDefault("purpose") == "support-request"); // the admins are told

        // Not before approval.
        Assert.Equal(HttpStatusCode.Conflict, (await ops.PostApiAsync($"/api/ops/support/{tenantId}/{session.Id}/enter", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/support/{session.Id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ops.PostApiAsync($"/api/ops/support/{tenantId}/{session.Id}/enter", new { })).StatusCode);

        // Inside, as a Viewer: reading works and is logged; changing anything does not.
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostApiAsync("/api/members/invitations", new { email = "x@y.test", role = "TenantAdmin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/audit")).StatusCode);       // not the tenant's admin
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/support")).StatusCode);     // cannot approve itself

        var log = await admin.GetFromJsonAsync<AuditPage>("/api/audit?action=support");
        Assert.Contains(log!.Entries, e => e.Action == "support.viewed" && e.Summary == "Viewed /api/horses" && e.ActorKind == "Operator");
        Assert.Contains(log.Entries, e => e.Action == "support.approved");
        Assert.Contains(log.Entries, e => e.Action == "support.entered");

        // The admin ends it; the very next request is refused, and the way out still works.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/support/{session.Id}/end", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/horses")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ops.PostApiAsync("/api/ops/support/exit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/api/ops/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.GetAsync("/api/horses")).StatusCode); // back to no tenant at all
    }

    [Fact]
    public async Task A_declined_request_never_opens()
    {
        var (tenantId, admin) = await NewTenantAsync();
        var (ops, _) = await Api.SignedInOperatorAsync();
        var session = (await (await ops.PostApiAsync($"/api/ops/tenants/{tenantId}/support", new { reason = "Checking the import", hours = 1 })).Content.ReadFromJsonAsync<SupportView>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/support/{session.Id}/decline", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ops.PostApiAsync($"/api/ops/support/{tenantId}/{session.Id}/enter", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostApiAsync($"/api/support/{session.Id}/approve", new { })).StatusCode); // answered once
    }

    [Fact]
    public async Task Sign_ins_failures_and_role_changes_are_logged_for_the_tenant_admin()
    {
        var (tenantId, admin) = await NewTenantAsync();
        var (email, password) = await Api.CreatePersonAsync(tenantId, MemberRole.Viewer);

        using (var browser = Api.Browser())
            await browser.PostApiAsync("/api/auth/password", new { email, password = "not the password" });
        var member = (await admin.GetFromJsonAsync<MembersPage>("/api/members"))!.Members.Single(m => m.Email == email);
        await admin.PostApiAsync($"/api/members/{member.Id}/role", new { role = "Coordinator" });

        var log = (await admin.GetFromJsonAsync<AuditPage>("/api/audit"))!.Entries;
        Assert.Contains(log, e => e.Action == "auth.signed-in" && e.ActorKind == "Staff");
        Assert.Contains(log, e => e.Action == "auth.sign-in-failed" && e.Who == email);
        var change = log.Single(e => e.Action == "member.role-changed");
        Assert.Equal(("Viewer", "Coordinator"), (change.Before, change.After));

        var csv = await admin.GetStringAsync("/api/audit/export.csv");
        Assert.StartsWith("﻿At (UTC),Who,Kind,Action", csv);
        Assert.Contains("member.role-changed", csv);

        // Only the tenant admin reads it.
        using var viewer = await Api.SignedInAsync(tenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task The_audit_log_cannot_be_edited_or_deleted()
    {
        await using var context = db.ContextFor(db.A.TenantId);
        var entry = await context.AuditEntries.FirstAsync();

        context.AuditEntries.Remove(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    /// <summary>A tenant of its own with a signed-in tenant admin.</summary>
    private async Task<(Guid TenantId, HttpClient Admin)> NewTenantAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Support {suffix}", $"su{suffix}");
        await using (var context = db.ContextFor(tenant.Id))
        {
            context.Add(tenant);
            await context.SaveChangesAsync();
        }

        return (tenant.Id, await Api.SignedInAsync(tenant.Id, MemberRole.TenantAdmin));
    }
}
