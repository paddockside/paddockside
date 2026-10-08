using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Operators;

/// <summary>
/// Support sessions (identity-access.md §7). An operator asks a tenant for access, with a reason and a duration;
/// one of the tenant's admins approves or declines; while it lasts the operator sees the tenant as a Viewer —
/// read-only — and every page they view is written to the tenant's audit log. Either side can end it early.
/// </summary>
public static class SupportSessions
{
    public sealed record SupportRequest(string Reason, int Hours, bool Emergency = false);

    public sealed record SupportView(Guid Id, Guid TenantId, string Tenant, string Operator, string Reason, string Status,
        string Requested, string? Decided, string? DecidedBy, string? Until, string? EndedBy, bool Emergency);

    public static void MapSupportSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/ops").RequireAuthorization(OperatorPolicy.Name);
        ops.MapPost("/tenants/{tenantId:guid}/support", Request);
        ops.MapGet("/support", OperatorList);
        ops.MapGet("/support/emergency", PendingEmergencies);
        ops.MapPost("/support/{tenantId:guid}/{id:guid}/second", SecondOperator);
        ops.MapPost("/support/{tenantId:guid}/{id:guid}/enter", Enter);
        ops.MapPost("/support/{tenantId:guid}/{id:guid}/end", (Guid tenantId, Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            EndAsync(tenantId, id, OperatorName(user), mustBeOperator: PersonOf(user), tenants, audit, clock, ct));
        ops.MapPost("/support/exit", Exit);

        var support = app.MapGroup("/api/support").RequireAuthorization(StaffPolicy.Name);
        support.MapGet("/", TenantList);
        support.MapPost("/{id:guid}/approve", (Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            DecideAsync(id, approve: true, user, tenants, audit, clock, ct));
        support.MapPost("/{id:guid}/decline", (Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            DecideAsync(id, approve: false, user, tenants, audit, clock, ct));
        support.MapPost("/{id:guid}/end", (Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            IsTenantAdmin(user) ? EndAsync(TenantOf(user), id, user.FindFirstValue(ClaimTypes.Email) ?? "A tenant admin", mustBeOperator: null, tenants, audit, clock, ct) : Task.FromResult(AdminsOnly()));
    }

    private static Guid PersonOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static Guid TenantOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionClaims.Tenant)!);

    private static string OperatorName(ClaimsPrincipal user) => $"Paddockside support: {user.FindFirstValue(ClaimTypes.Email) ?? user.Identity!.Name}";

    /// <summary>A tenant admin acting for themselves — never an operator inside the tenant on a support session.</summary>
    private static bool IsTenantAdmin(ClaimsPrincipal user) =>
        user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin) && !user.HasClaim(c => c.Type == SessionClaims.SupportSession);

    private static IResult AdminsOnly() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can answer support requests.");

    private static SupportView View(SupportSession s, string tenantName, DateTimeOffset now) => new(
        s.Id, s.TenantId, tenantName, s.OperatorName, s.Reason, s.StatusAt(now).ToString(),
        Words.DayAndTime(s.RequestedAt, now), s.DecidedAt is { } d ? Words.DayAndTime(d, now) : null, s.DecidedBy,
        s.ExpiresAt is { } e ? Words.DayAndTime(e, now) : null, s.EndedBy, s.Emergency);

    // ---- the operator's side -----------------------------------------------------------------------------------

    private static async Task<IResult> Request(Guid tenantId, SupportRequest request, ClaimsPrincipal user, TenantScopedDb tenants,
        PaddocksideIdentityDbContext identity, IEmailSender email, IOptions<EmailOptions> options, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var hours = request.Hours == 0 ? (int)SupportSession.DefaultDuration.TotalHours : request.Hours;
        if (hours is < 1 or > 24) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Ask for between 1 and 24 hours.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Say why you need access; their admin reads it before approving.");

        await using var db = tenants.For(tenantId);
        if (await db.Tenants.SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        var session = new SupportSession(tenantId, PersonOf(user), OperatorName(user), request.Reason, TimeSpan.FromHours(hours), clock.GetUtcNow(), request.Emergency);
        db.SupportSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        if (session.Emergency)
        {
            // No admin to ask: a second operator approves, and the tenant is told when it starts (SecondOperator).
            await audit.RecordAsync(tenantId, "support.emergency-requested",
                $"{session.OperatorName} asked for {Words.Hours(hours)} of emergency access (no admin could be reached): {session.Reason}", "SupportSession", session.Id, kind: AuditActorKind.Operator, cancellationToken: cancellationToken);
            return Results.Created($"/api/ops/support/{tenantId}/{session.Id}", View(session, tenant.Name, clock.GetUtcNow()));
        }

        await audit.RecordAsync(tenantId, "support.requested", $"{session.OperatorName} asked for {Words.Hours(hours)} of access: {session.Reason}", "SupportSession", session.Id, kind: AuditActorKind.Operator, cancellationToken: cancellationToken);
        var link = $"{options.Value.PortalBaseUrl?.TrimEnd('/')}/members";
        await EmailAdminsAsync(identity, email, options.Value, tenantId, $"Paddockside support has asked to view {tenant.Name}",
            $"{session.OperatorName} has asked to view {tenant.Name}'s account for {Words.Hours(hours)}, to help with:\n\n{session.Reason}\n\n" +
            $"Nothing happens unless you approve it. Approve or decline it on the Members page: {link}\n\n" +
            "While it lasts they can read but not change anything, everything they look at is recorded in your audit log, and you can end it at any time.",
            "support-request", cancellationToken);
        return Results.Created($"/api/ops/support/{tenantId}/{session.Id}", View(session, tenant.Name, clock.GetUtcNow()));
    }

    /// <summary>Emergency requests from other operators, waiting for a second operator (identity-access.md §7).</summary>
    private static async Task<IResult> PendingEmergencies(ClaimsPrincipal user, TenantScopedDb tenants, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var me = PersonOf(user);
        var pending = await tenants.PendingEmergencySessionsAsync(cancellationToken);
        return Results.Ok(pending.Where(p => p.Session.OperatorPersonId != me).Select(p => View(p.Session, p.TenantName, now)).ToList());
    }

    /// <summary>
    /// The second operator approves emergency access. It starts now, and the tenant's admins are told at once, with
    /// both operators named, so access without them is never silent.
    /// </summary>
    private static async Task<IResult> SecondOperator(Guid tenantId, Guid id, ClaimsPrincipal user, TenantScopedDb tenants, PaddocksideIdentityDbContext identity,
        IEmailSender email, IOptions<EmailOptions> options, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var session = await db.SupportSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var tenant = await db.Tenants.SingleOrDefaultAsync(cancellationToken);
        if (session is null || tenant is null) return Results.NotFound();
        try
        {
            session.ApproveAsSecondOperator(PersonOf(user), OperatorName(user), clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenantId, "support.emergency-approved",
            $"{OperatorName(user)} approved {session.OperatorName}'s emergency access until {Words.Moment(session.ExpiresAt!.Value)}", "SupportSession", session.Id,
            kind: AuditActorKind.Operator, cancellationToken: cancellationToken);

        await EmailAdminsAsync(identity, email, options.Value, tenantId, $"Paddockside has opened emergency access to {tenant.Name}",
            $"We could not reach an admin at {tenant.Name}, so two of Paddockside's operators have opened emergency access to your account.\n\n" +
            $"Reason: {session.Reason}\nAsked by: {session.OperatorName}\nApproved by: {OperatorName(user)}\nUntil: {Words.Moment(session.ExpiresAt!.Value)} (Sydney time)\n\n" +
            "They can read but not change anything, and everything they look at is recorded in your audit log. " +
            $"You can end it now on the Members page: {options.Value.PortalBaseUrl?.TrimEnd('/')}/members",
            "support-emergency", cancellationToken);
        return Results.NoContent();
    }

    private static async Task EmailAdminsAsync(PaddocksideIdentityDbContext identity, IEmailSender email, EmailOptions options, Guid tenantId,
        string subject, string text, string purpose, CancellationToken cancellationToken)
    {
        var admins = await (from m in identity.Memberships
                            join p in identity.Users on m.PersonId equals p.Id
                            where m.TenantId == tenantId && m.Role == MemberRole.TenantAdmin && m.Status == MembershipStatus.Active
                            select p.Email).ToListAsync(cancellationToken);
        var html = $"<p>{System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>")}</p>";
        foreach (var admin in admins.OfType<string>())
            await email.SendAsync(new OutboundEmail(options.FromAddress, Senders.Product, admin, string.Empty, options.FromAddress,
                subject, html, text, new Dictionary<string, string> { ["purpose"] = purpose }, TrackOpens: false), cancellationToken);
    }

    private static async Task<IResult> OperatorList(ClaimsPrincipal user, TenantScopedDb tenants, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var sessions = await tenants.SupportSessionsForOperatorAsync(PersonOf(user), cancellationToken);
        return Results.Ok(sessions.Select(s => View(s.Session, s.TenantName, now)).ToList());
    }

    /// <summary>Into the tenant as a Viewer, for as long as the approved session lasts.</summary>
    private static async Task<IResult> Enter(Guid tenantId, Guid id, ClaimsPrincipal user, HttpContext http, TenantScopedDb tenants,
        UserManager<Person> users, SignInManager<Person> signIn, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var session = await db.SupportSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null || session.OperatorPersonId != PersonOf(user)) return Results.NotFound();
        if (!session.IsActive(clock.GetUtcNow()))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That support session is not active: it has not been approved, or it has ended.");

        var person = await users.FindByIdAsync(PersonOf(user).ToString());
        var principal = await signIn.CreateUserPrincipalAsync(person!);
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var type in new[] { SessionClaims.Tenant, SessionClaims.Role, SessionClaims.AudienceClass, SessionClaims.SupportSession })
            foreach (var claim in identity.FindAll(type).ToList()) identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(SessionClaims.Tenant, tenantId.ToString()));
        identity.AddClaim(new Claim(SessionClaims.Role, nameof(MemberRole.Viewer)));
        identity.AddClaim(new Claim(SessionClaims.AudienceClass, nameof(AudienceClass.Staff)));
        identity.AddClaim(new Claim(SessionClaims.SupportSession, session.Id.ToString()));
        identity.AddClaim(new Claim("amr", "mfa"));
        await http.SignInAsync(IdentityConstants.ApplicationScheme, principal, new AuthenticationProperties { IsPersistent = true });

        await audit.RecordAsync(tenantId, "support.entered", $"{session.OperatorName} entered the account (read-only) until {Words.Moment(session.ExpiresAt!.Value)}",
            "SupportSession", session.Id, actor: (person!.Id, session.OperatorName), kind: AuditActorKind.Operator, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Back out of the tenant to the operator's own session.</summary>
    private static async Task<IResult> Exit(ClaimsPrincipal user, UserManager<Person> users, SignInManager<Person> signIn, AuditLog audit, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(user.FindFirstValue(SessionClaims.SupportSession), out var sessionId) && Guid.TryParse(user.FindFirstValue(SessionClaims.Tenant), out var tenantId))
            await audit.RecordAsync(tenantId, "support.left", "Left the account", "SupportSession", sessionId, cancellationToken: cancellationToken);
        var person = await users.FindByIdAsync(PersonOf(user).ToString());
        await signIn.SignInWithClaimsAsync(person!, isPersistent: true, [new Claim("amr", "mfa")]);
        return Results.NoContent();
    }

    // ---- the tenant's side -------------------------------------------------------------------------------------

    private static async Task<IResult> TenantList(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsTenantAdmin(user)) return AdminsOnly();
        var now = clock.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var sessions = await db.SupportSessions.AsNoTracking().OrderByDescending(s => s.RequestedAt).Take(50).ToListAsync(cancellationToken);
        return Results.Ok(sessions.Select(s => View(s, tenant.Name, now)).ToList());
    }

    private static async Task<IResult> DecideAsync(Guid id, bool approve, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsTenantAdmin(user)) return AdminsOnly();
        await using var db = tenants.For(TenantOf(user));
        var session = await db.SupportSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null) return Results.NotFound();
        if (session.DecidedAt is not null) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This request has already been answered.");

        var by = user.FindFirstValue(ClaimTypes.Email) ?? "A tenant admin";
        var now = clock.GetUtcNow();
        try
        {
            if (approve) session.Approve(PersonOf(user), by, now);
            else session.Decline(by, now);
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(session.TenantId, approve ? "support.approved" : "support.declined",
            approve ? $"Approved {session.OperatorName}'s access until {Words.Moment(session.ExpiresAt!.Value)}" : $"Declined {session.OperatorName}'s request",
            "SupportSession", session.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> EndAsync(Guid tenantId, Guid id, string by, Guid? mustBeOperator, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var session = await db.SupportSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null || (mustBeOperator is { } op && session.OperatorPersonId != op)) return Results.NotFound();
        session.End(by, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenantId, "support.ended", $"{by} ended the support session", "SupportSession", session.Id,
            kind: mustBeOperator is null ? null : AuditActorKind.Operator, cancellationToken: cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>
/// Every request made inside a support session: refused once the session has ended or expired, refused if it would
/// change anything (support access is read-only), and otherwise recorded in the tenant's audit log — "every page
/// they view is written to the tenant's audit log" (identity-access.md §7).
/// </summary>
public sealed class SupportSessionGuard(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantScopedDb tenants, AuditLog audit, TimeProvider clock)
    {
        var user = http.User;
        if (!Guid.TryParse(user.FindFirstValue(SessionClaims.SupportSession), out var sessionId) || !http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }

        // The way out always works, even after the session has ended.
        var path = http.Request.Path;
        if (path.StartsWithSegments("/api/ops") || path.StartsWithSegments("/api/auth/sign-out"))
        {
            await next(http);
            return;
        }

        var tenantId = Guid.Parse(user.FindFirstValue(SessionClaims.Tenant)!);
        await using (var db = tenants.For(tenantId))
        {
            var session = await db.SupportSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId);
            var personId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            if (session is null || session.OperatorPersonId != personId || !session.IsActive(clock.GetUtcNow()))
            {
                await Refuse(http, "Your support session has ended. Go back to the operator console.");
                return;
            }
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            await Refuse(http, "Support access is read-only.");
            return;
        }

        if (!path.StartsWithSegments("/api/auth/me"))
            await audit.RecordAsync(tenantId, "support.viewed", $"Viewed {path}{http.Request.QueryString}", "SupportSession", sessionId);
        await next(http);
    }

    private static Task Refuse(HttpContext http, string title)
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        return http.Response.WriteAsJsonAsync(new { title, status = 403 }, options: null, contentType: "application/problem+json");
    }
}
