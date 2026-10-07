using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Operators;

/// <summary>
/// The operator console (identity-access.md §7): Paddockside's own staff see every tenant's account and health —
/// name, size, members, email and inbound health — and onboard new tenants. They never see a tenant's horses,
/// parties, items or media; those need a support session the tenant approves, which comes later.
/// </summary>
public static class OperatorEndpoints
{
    public sealed record OperatorMe(string Email);

    public sealed record TenantRow(
        Guid Id, string Name, string Slug, string Created, int Horses, int Staff, int OwnersSignedIn, int InvitationsWaiting,
        int EmailsSent, int EmailsDelivered, int EmailsBounced, int EmailsWaiting, int InboundPending, int InboundHeld);

    public sealed record OperatorRow(string Email, bool IsYou);

    public sealed record OperatorsPage(IReadOnlyList<OperatorRow> Operators, IReadOnlyList<string> Invited);

    public sealed record NewTenant(string Name, string? Slug, string AdminEmail);

    public sealed record TenantCreated(Guid Id, string Slug, bool InvitationSent, string? Problem);

    public sealed record OperatorInvite(string Email);

    public static void MapOperatorEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/ops").RequireAuthorization(OperatorPolicy.Name);
        ops.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new OperatorMe(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity!.Name!)));
        ops.MapGet("/tenants", Tenants);
        ops.MapPost("/tenants", CreateTenant);
        ops.MapGet("/operators", Operators);
        ops.MapPost("/operators/invitations", InviteOperator);
    }

    private static Guid PersonOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Every tenant, with counts only (the rule in the class summary).</summary>
    private static async Task<IResult> Tenants(TenantScopedDb tenants, PaddocksideIdentityDbContext identity, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var overviews = await tenants.TenantOverviewsAsync(now.AddDays(-30), cancellationToken);
        var memberships = await identity.Memberships.Where(m => m.Status == MembershipStatus.Active)
            .GroupBy(m => new { m.TenantId, Owner = m.Role == MemberRole.Owner || m.Role == MemberRole.OwnerDelegate })
            .Select(g => new { g.Key.TenantId, g.Key.Owner, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var waiting = await identity.StaffInvitations.Where(i => i.TenantId != null && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .GroupBy(i => i.TenantId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        return Results.Ok(overviews.Select(t => new TenantRow(
            t.Id, t.Name, t.Slug, Words.Day(CreatedAt(t.Id), now), t.Horses,
            memberships.Where(m => m.TenantId == t.Id && !m.Owner).Sum(m => m.Count),
            memberships.Where(m => m.TenantId == t.Id && m.Owner).Sum(m => m.Count),
            waiting.GetValueOrDefault(t.Id),
            t.EmailsSent, t.EmailsDelivered, t.EmailsBounced, t.EmailsWaiting, t.InboundPending, t.InboundHeld)).ToList());
    }

    /// <summary>Onboards a tenant: creates it and invites its first tenant admin.</summary>
    private static async Task<IResult> CreateTenant(NewTenant request, ClaimsPrincipal user, TenantScopedDb tenants, StaffInvitations invitations,
        UserManager<Person> users, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 200) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the business's name.");
        var address = request.AdminEmail?.Trim() ?? "";
        if (!address.Contains('@') || address.Length > 320)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the email address of their first tenant admin.");

        var tenant = new Tenant(name, string.IsNullOrWhiteSpace(request.Slug) ? null : request.Slug);
        if (tenant.Slug.Length == 0)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose a short name of letters and digits for their email addresses.");
        if (await tenants.TenantIdForSlugAsync(tenant.Slug, cancellationToken) is not null)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"'{tenant.Slug}' is taken. Choose another short name.");

        await using (var db = tenants.For(tenant.Id))
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(cancellationToken);
        }

        var inviter = await users.FindByIdAsync(PersonOf(user).ToString());
        var sent = await invitations.SendAsync(tenant.Id, tenant.Name, address, MemberRole.TenantAdmin, inviter!.Id, "Paddockside", cancellationToken);
        return Results.Created($"/api/ops/tenants/{tenant.Id}", new TenantCreated(tenant.Id, tenant.Slug, sent.Sent, sent.Problem));
    }

    private static async Task<IResult> Operators(ClaimsPrincipal user, PaddocksideIdentityDbContext identity, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var me = PersonOf(user);
        var operators = await identity.Users.Where(p => p.IsOperator).OrderBy(p => p.Email).Select(p => new { p.Id, p.Email }).ToListAsync(cancellationToken);
        var invited = await identity.StaffInvitations.Where(i => i.ForOperator && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => i.Email).ToListAsync(cancellationToken);
        return Results.Ok(new OperatorsPage(operators.Select(o => new OperatorRow(o.Email ?? "", o.Id == me)).ToList(), invited));
    }

    private static async Task<IResult> InviteOperator(OperatorInvite request, ClaimsPrincipal user, StaffInvitations invitations, UserManager<Person> users,
        CancellationToken cancellationToken)
    {
        var address = request.Email?.Trim() ?? "";
        if (!address.Contains('@') || address.Length > 320)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the email address to invite.");
        if (await users.FindByEmailAsync(address) is { IsOperator: true })
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{address} is already an operator.");

        var inviter = await users.FindByIdAsync(PersonOf(user).ToString());
        var sent = await invitations.SendAsync(null, Application.Messaging.Senders.Product, address, MemberRole.Viewer, inviter!.Id,
            inviter.Email ?? "A Paddockside operator", cancellationToken);
        return Results.Created("/api/ops/operators", new TenantCreated(sent.Id, "", sent.Sent, sent.Problem));
    }

    /// <summary>When a tenant was created: its id is a version 7 GUID, whose first 48 bits are the creation time.</summary>
    private static DateTimeOffset CreatedAt(Guid id)
    {
        var bytes = id.ToByteArray(bigEndian: true);
        long milliseconds = 0;
        for (var i = 0; i < 6; i++) milliseconds = (milliseconds << 8) | bytes[i];
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
