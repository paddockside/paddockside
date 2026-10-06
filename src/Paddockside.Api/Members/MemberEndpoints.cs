using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Api.Auth;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Members;

/// <summary>
/// A tenant's staff (identity-access.md §5.2, §6): invite by email with a role, change roles, suspend and
/// reactivate. Tenant admins only — "members.manage" is theirs alone — and always within their own tenant. Every
/// tenant keeps at least one active Tenant admin: the last one cannot be demoted or suspended, only replaced.
/// </summary>
public static class MemberEndpoints
{
    public sealed record MemberView(Guid Id, string Email, string Role, string RoleLabel, string Status, string? Since, bool IsYou);

    public sealed record InvitationView(Guid Id, string Email, string Role, string RoleLabel, string InvitedBy, string Sent, string Expires);

    public sealed record RoleOption(string Role, string Label, string Description);

    public sealed record MembersPage(IReadOnlyList<MemberView> Members, IReadOnlyList<InvitationView> Invitations, IReadOnlyList<RoleOption> Roles);

    public sealed record InviteRequest(string Email, string Role);

    public sealed record RoleRequest(string Role);

    public sealed record Invited(Guid Id, bool Sent, string? Problem);

    public sealed record JoinRequest(string Token);

    public sealed record AcceptRequest(string Token, string Password);

    /// <summary>"new-password" for someone new to Paddockside (or an owner, who has none); "existing-password" otherwise.</summary>
    public sealed record JoinDetails(string TenantName, string Email, string Role, string RoleDescription, string InvitedBy, string Needs);

    public static void MapMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var members = app.MapGroup("/api/members").RequireAuthorization(StaffPolicy.Name);
        members.MapGet("/", List);
        members.MapPost("/invitations", Invite);
        members.MapPost("/invitations/{id:guid}/revoke", Revoke);
        members.MapPost("/{id:guid}/role", ChangeRole);
        members.MapPost("/{id:guid}/suspend", (Guid id, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, CancellationToken ct) => SetStatus(id, MembershipStatus.Suspended, user, identity, ct));
        members.MapPost("/{id:guid}/reactivate", (Guid id, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, CancellationToken ct) => SetStatus(id, MembershipStatus.Active, user, identity, ct));

        var join = app.MapGroup("/api/join").AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        join.MapPost("/inspect", Inspect);
        join.MapPost("/accept", Accept);
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin);

    private static Guid TenantOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionClaims.Tenant)!);

    private static Guid PersonOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static IResult AdminsOnly() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can manage members.");

    private static IResult Refused(string title) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title);

    private static async Task<IResult> List(ClaimsPrincipal user, PaddocksideIdentityDbContext identity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        var tenantId = TenantOf(user);
        var me = PersonOf(user);
        var now = clock.GetUtcNow();

        var members = await (from m in identity.Memberships
                             join p in identity.Users on m.PersonId equals p.Id
                             where m.TenantId == tenantId
                             select new { m, p.Email, p.UserName })
            .ToListAsync(cancellationToken);
        var invitations = await identity.StaffInvitations
            .Where(i => i.TenantId == tenantId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(new MembersPage(
            members.Where(x => MemberRoles.ClassOf(x.m.Role) == AudienceClass.Staff)
                .OrderBy(x => MemberRoles.StaffRoles.ToList().IndexOf(x.m.Role)).ThenBy(x => x.Email)
                .Select(x => new MemberView(x.m.Id, x.Email ?? x.UserName ?? "", x.m.Role.ToString(), MemberRoles.Label(x.m.Role), x.m.Status.ToString(),
                    x.m.AcceptedAt is { } at ? Words.Day(at, now) : Words.Day(x.m.CreatedAt, now), x.m.PersonId == me))
                .ToList(),
            invitations.Select(i => new InvitationView(i.Id, i.Email, i.Role.ToString(), MemberRoles.Label(i.Role), i.InvitedByName,
                Words.Day(i.CreatedAt, now), Words.Day(i.ExpiresAt, now))).ToList(),
            MemberRoles.StaffRoles.Select(r => new RoleOption(r.ToString(), MemberRoles.Label(r), MemberRoles.Describe(r))).ToList()));
    }

    private static async Task<IResult> Invite(
        InviteRequest request,
        ClaimsPrincipal user,
        PaddocksideIdentityDbContext identity,
        PaddocksideDbContext db,
        UserManager<Person> users,
        IEmailSender email,
        OwnerEmailRenderer renderer,
        IOptions<EmailOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        if (!Enum.TryParse<MemberRole>(request.Role, out var role) || !MemberRoles.StaffRoles.Contains(role))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose one of the staff roles.");
        var address = request.Email?.Trim() ?? "";
        if (address.Length is < 3 or > 320 || !address.Contains('@') || address.StartsWith('@') || address.EndsWith('@'))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the email address the invitation should go to.");
        if (options.Value.PortalBaseUrl is not { Length: > 0 } baseUrl)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Invitations need the portal address configured (Email:PortalBaseUrl).");

        var tenantId = TenantOf(user);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        if (await users.FindByEmailAsync(address) is { } existing
            && await identity.Memberships.AnyAsync(m => m.PersonId == existing.Id && m.TenantId == tenantId && m.Status == MembershipStatus.Active
                && (m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin), cancellationToken))
            return Refused($"{address} is already a member. Change their role in the list instead.");

        // Inviting again replaces the earlier invitation: only the newest link works.
        var now = clock.GetUtcNow();
        foreach (var earlier in await identity.StaffInvitations.Where(i => i.TenantId == tenantId && i.Email == address && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(cancellationToken))
            earlier.RevokedAt = now;

        var inviter = await users.FindByIdAsync(PersonOf(user).ToString());
        var token = SignInToken.NewLinkToken();
        var invitation = new StaffInvitation
        {
            TenantId = tenantId,
            TenantName = tenant.Name,
            Email = address,
            Role = role,
            InvitedByPersonId = inviter!.Id,
            InvitedByName = inviter.Email ?? inviter.UserName ?? "A tenant admin",
            TokenHash = SignInToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + StaffInvitation.Lifetime,
        };
        identity.StaffInvitations.Add(invitation);
        await identity.SaveChangesAsync(cancellationToken);

        var rendered = renderer.RenderStaffInvitation(tenant.Name, invitation.InvitedByName, role, $"{baseUrl.TrimEnd('/')}/join#{token}");
        var result = await email.SendAsync(new OutboundEmail(options.Value.FromAddress, tenant.Name, address, string.Empty, invitation.InvitedByName,
            rendered.Subject, rendered.Html, rendered.Text, new Dictionary<string, string> { ["purpose"] = "staff-invitation" }), cancellationToken);

        return Results.Created($"/api/members/invitations/{invitation.Id}",
            new Invited(invitation.Id, result.Accepted, result.Accepted ? null : $"The email was not sent: {result.Error}. Cancel it and try again."));
    }

    private static async Task<IResult> Revoke(Guid id, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        var invitation = await identity.StaffInvitations.SingleOrDefaultAsync(i => i.Id == id && i.TenantId == TenantOf(user), cancellationToken);
        if (invitation is null) return Results.NotFound();
        if (invitation.AcceptedAt is not null) return Refused("That invitation was already accepted.");
        invitation.RevokedAt ??= clock.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeRole(Guid id, RoleRequest request, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        if (!Enum.TryParse<MemberRole>(request.Role, out var role) || !MemberRoles.StaffRoles.Contains(role))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose one of the staff roles.");
        var membership = await StaffMembershipAsync(identity, id, TenantOf(user), cancellationToken);
        if (membership is null) return Results.NotFound();
        if (membership.PersonId == PersonOf(user)) return Refused("You can't change your own role. Ask another tenant admin.");
        if (membership.Role == MemberRole.TenantAdmin && role != MemberRole.TenantAdmin && await IsLastAdminAsync(identity, membership, cancellationToken))
            return Refused("This is the only tenant admin. Make someone else a tenant admin first.");

        membership.Role = role;
        await identity.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Suspension ends their staff access within a minute (sessions are rechecked every minute).</summary>
    private static async Task<IResult> SetStatus(Guid id, MembershipStatus status, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        var membership = await StaffMembershipAsync(identity, id, TenantOf(user), cancellationToken);
        if (membership is null) return Results.NotFound();
        if (status == MembershipStatus.Suspended)
        {
            if (membership.PersonId == PersonOf(user)) return Refused("You can't suspend yourself. Ask another tenant admin.");
            if (membership.Role == MemberRole.TenantAdmin && await IsLastAdminAsync(identity, membership, cancellationToken))
                return Refused("This is the only tenant admin. Make someone else a tenant admin first.");
        }

        membership.Status = status;
        await identity.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static Task<Membership?> StaffMembershipAsync(PaddocksideIdentityDbContext identity, Guid id, Guid tenantId, CancellationToken cancellationToken) =>
        identity.Memberships.SingleOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId
            && (m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin), cancellationToken);

    private static async Task<bool> IsLastAdminAsync(PaddocksideIdentityDbContext identity, Membership membership, CancellationToken cancellationToken) =>
        !await identity.Memberships.AnyAsync(m => m.TenantId == membership.TenantId && m.Id != membership.Id
            && m.Role == MemberRole.TenantAdmin && m.Status == MembershipStatus.Active, cancellationToken);

    // ---- accepting --------------------------------------------------------------------------------------------

    private static async Task<StaffInvitation?> OpenInvitationAsync(PaddocksideIdentityDbContext identity, string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = SignInToken.Hash(token ?? "");
        var invitation = await identity.StaffInvitations.SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        return invitation is not null && invitation.IsOpen(now) ? invitation : null;
    }

    private static IResult Gone() => Results.Problem(statusCode: StatusCodes.Status410Gone,
        title: "This invitation has been used, cancelled or has expired. Ask the person who invited you to send a new one.");

    private static async Task<IResult> Inspect(JoinRequest request, PaddocksideIdentityDbContext identity, UserManager<Person> users, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await OpenInvitationAsync(identity, request.Token, clock.GetUtcNow(), cancellationToken) is not { } invitation) return Gone();
        var person = await users.FindByEmailAsync(invitation.Email);
        var needs = person is not null && await users.HasPasswordAsync(person) ? "existing-password" : "new-password";
        return Results.Ok(new JoinDetails(invitation.TenantName, invitation.Email, MemberRoles.Label(invitation.Role), MemberRoles.Describe(invitation.Role), invitation.InvitedByName, needs));
    }

    /// <summary>
    /// Accepting: a new person chooses a password (checked like any staff password, breached list included); someone
    /// who already has one proves it; an owner, who has none, sets one. Then the second factor, exactly as at sign-in.
    /// </summary>
    private static async Task<IResult> Accept(
        AcceptRequest request,
        HttpContext http,
        PaddocksideIdentityDbContext identity,
        UserManager<Person> users,
        SignInManager<Person> signIn,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (await OpenInvitationAsync(identity, request.Token, now, cancellationToken) is not { } invitation) return Gone();
        var password = request.Password ?? "";

        var person = await users.FindByEmailAsync(invitation.Email);
        if (person is null)
        {
            person = new Person { UserName = invitation.Email, Email = invitation.Email, EmailConfirmed = true };
            if (Problem(await users.CreateAsync(person, password)) is { } problem) return problem;
        }
        else if (await users.HasPasswordAsync(person))
        {
            var check = await signIn.CheckPasswordSignInAsync(person, password, lockoutOnFailure: true);
            if (check.IsLockedOut) return Results.Problem(statusCode: StatusCodes.Status423Locked, title: "Too many attempts. Wait 15 minutes, then try again.");
            if (!check.Succeeded) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "That is not your Paddockside password.");
        }
        else if (Problem(await users.AddPasswordAsync(person, password)) is { } problem)
        {
            return problem;
        }

        if (!person.EmailConfirmed)
        {
            person.EmailConfirmed = true; // the link proved the inbox
            await users.UpdateAsync(person);
        }

        var membership = await identity.Memberships.SingleOrDefaultAsync(m => m.PersonId == person.Id && m.TenantId == invitation.TenantId
            && (m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin), cancellationToken);
        if (membership is null)
            identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = invitation.TenantId, Role = invitation.Role, InvitedByPersonId = invitation.InvitedByPersonId, AcceptedAt = now });
        else
            (membership.Role, membership.Status, membership.AcceptedAt) = (invitation.Role, MembershipStatus.Active, membership.AcceptedAt ?? now);

        (invitation.AcceptedAt, invitation.AcceptedByPersonId) = (now, person.Id);
        await identity.SaveChangesAsync(cancellationToken);

        await http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, AuthEndpoints.PendingSecondFactor(person));
        return Results.Ok(new AuthEndpoints.NextStep(person.TwoFactorEnabled ? "totp" : "enrol"));
    }

    private static IResult? Problem(IdentityResult result) => result.Succeeded
        ? null
        : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: string.Join(" ", result.Errors.Select(e => e.Description)));
}
