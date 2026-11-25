using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Api.Audit;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Media;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Account;

/// <summary>
/// The Account page: the name each staff member's messages are signed with, and, for tenant admins, how the
/// business's emails look (identity-access.md §5.2: branding is the tenant admin's).
/// </summary>
public static class AccountEndpoints
{
    public sealed record AccountView(string? YourName, string Email, bool CanChangeBrand, Branding.BrandView Brand);

    public sealed record NameRequest(string? Name);

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account").RequireAuthorization(StaffPolicy.Name);
        account.MapGet("/", Get);
        account.MapPut("/name", SaveName);
        account.MapPut("/look", SaveLook);
        foreach (var square in new[] { false, true })
        {
            var logo = square ? "/square-logo" : "/logo";
            account.MapPost(logo, (HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, AuditLog audit, ILoggerFactory loggers, CancellationToken ct) =>
                    SaveLogo(request, user, db, store, audit, loggers, square, ct))
                .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Pictures.MaxLogoBytes + 64 * 1024))
                .DisableAntiforgery(); // the API's own request header guards unsafe requests (RequireApiRequestHeader)
            account.MapPost($"{logo}/remove", (ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, CancellationToken ct) => RemoveLogo(user, db, audit, square, ct));
        }
        account.MapPost("/sample", (Branding.SampleRequest request, PaddocksideDbContext db, OwnerEmailRenderer renderer, IOptions<EmailOptions> options, HttpRequest http,
            TimeProvider clock, CancellationToken ct) => Branding.SampleAsync(db, request, renderer, Branding.BaseUrl(options.Value, http), clock, ct));
    }

    private static bool IsAdmin(ClaimsPrincipal user) => user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin);

    private static IResult AdminsOnly() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can change how the business's emails look.");

    /// <summary>The person signed in, unless Paddockside support is looking in: a name is the member's own.</summary>
    private static Guid? PersonId(ClaimsPrincipal user) =>
        !user.HasClaim(c => c.Type == SessionClaims.SupportSession) && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<IResult> Get(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var name = PersonId(user) is { } person
            ? await db.StaffSignatures.AsNoTracking().Where(s => s.PersonId == person).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        return Results.Ok(new AccountView(name, user.FindFirstValue(ClaimTypes.Email) ?? "", IsAdmin(user), Branding.View(tenant)));
    }

    private static async Task<IResult> SaveName(NameRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (PersonId(user) is not { } person) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in as yourself to set your name.");
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        try
        {
            var mine = await db.StaffSignatures.SingleOrDefaultAsync(s => s.PersonId == person, cancellationToken);
            if (mine is null) db.StaffSignatures.Add(mine = new StaffSignature(tenant.Id, person, "", now));
            mine.SetName(request.Name, now);
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveLook(Branding.LookRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        return await Recorded(await Branding.SaveLookAsync(db, request, cancellationToken), "Changed the colour or business details on emails", db, audit, cancellationToken);
    }

    private static async Task<IResult> SaveLogo(HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, AuditLog audit,
        ILoggerFactory loggers, bool square, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        return await Recorded(await Branding.SaveLogoAsync(db, request, store, loggers.CreateLogger("Branding"), cancellationToken, square), square ? "Uploaded a square logo" : "Uploaded a logo", db, audit, cancellationToken);
    }

    private static async Task<IResult> RemoveLogo(ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, bool square, CancellationToken cancellationToken)
    {
        if (!IsAdmin(user)) return AdminsOnly();
        return await Recorded(await Branding.RemoveLogoAsync(db, cancellationToken, square), square ? "Removed the square logo" : "Removed the logo", db, audit, cancellationToken);
    }

    private static async Task<IResult> Recorded(IResult result, string what, PaddocksideDbContext db, AuditLog audit, CancellationToken cancellationToken)
    {
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK })
        {
            var tenantId = await db.Tenants.Select(t => t.Id).SingleAsync(cancellationToken);
            await audit.RecordAsync(tenantId, "branding.changed", what, "Tenant", tenantId, cancellationToken: cancellationToken);
        }

        return result;
    }
}
