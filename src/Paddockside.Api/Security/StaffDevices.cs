using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Api.Security;

/// <summary>
/// Staff phones that stay signed in (<see cref="StaffDevice"/>). The session carries the device's id in its protected
/// properties and, as a claim, that it is one, which the operator console refuses: that always needs a fresh sign-in.
/// Checked with the rest of the session once a minute, so a phone signed out is out within a minute.
/// </summary>
public static class StaffDevices
{
    private const string DeviceKey = "paddockside.staff-device";

    public sealed record DeviceView(Guid Id, string Label, string SignedIn, string LastUsed, bool ThisOne);

    /// <summary>Signs a full staff session in on a phone its owner said is their own.</summary>
    public static async Task<AuthenticationProperties> StartAsync(HttpContext http, PaddocksideIdentityDbContext identity, Person person, DateTimeOffset now)
    {
        var device = new StaffDevice { PersonId = person.Id, Label = StaffDevice.LabelFor(http.Request.Headers.UserAgent.ToString()), CreatedAt = now, LastSeenAt = now };
        identity.StaffDevices.Add(device);
        await identity.SaveChangesAsync();
        var properties = new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = now + StaffDevice.IdleTimeout };
        properties.Items[DeviceKey] = device.Id.ToString();
        return properties;
    }

    public static Guid? Of(AuthenticationProperties? properties) =>
        properties?.Items.TryGetValue(DeviceKey, out var value) == true && Guid.TryParse(value, out var id) ? id : null;

    /// <summary>
    /// The properties to sign the same session in again with, after it moves (to another business, or out of a support
    /// session): the same phone, the same week, and never a later end than it already had.
    /// </summary>
    public static AuthenticationProperties Carry(AuthenticationProperties? from, DateTimeOffset now)
    {
        var properties = new AuthenticationProperties { IsPersistent = true };
        if (from is null) return properties;
        foreach (var (key, value) in from.Items.Where(i => i.Key.StartsWith("paddockside.", StringComparison.Ordinal))) properties.Items[key] = value;
        if (Of(from) is not null) (properties.AllowRefresh, properties.ExpiresUtc) = (true, now + StaffDevice.IdleTimeout);
        return properties;
    }

    /// <summary>False when the session's phone has been signed out. Notes it's in use, at most once an hour.</summary>
    public static async Task<bool> StillSignedInAsync(AuthenticationProperties properties, PaddocksideIdentityDbContext identity, DateTimeOffset now)
    {
        if (Of(properties) is not { } id) return true;
        if (await identity.StaffDevices.SingleOrDefaultAsync(d => d.Id == id) is not { RevokedAt: null } device) return false;
        if (now - device.LastSeenAt > TimeSpan.FromHours(1))
        {
            device.LastSeenAt = now;
            await identity.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>These people's phones that are signed in now.</summary>
    public static async Task<ILookup<Guid, DeviceView>> ForAsync(IReadOnlyCollection<Guid> personIds, Guid? thisOne, PaddocksideIdentityDbContext identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var oldest = now - StaffSessionCookie.AbsoluteLifetime;
        var devices = await identity.StaffDevices.AsNoTracking().Where(d => personIds.Contains(d.PersonId) && d.RevokedAt == null && d.CreatedAt > oldest).ToListAsync(cancellationToken);
        return devices.Where(d => d.IsLive(now, StaffSessionCookie.AbsoluteLifetime)).OrderByDescending(d => d.LastSeenAt)
            .ToLookup(d => d.PersonId, d => new DeviceView(d.Id, d.Label, Words.Day(d.CreatedAt, now), Words.Recently(d.LastSeenAt, now), d.Id == thisOne));
    }

    public static void MapStaffDevices(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/account/phones", Mine).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/account/phones/{id:guid}/sign-out", SignOutMine).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/members/phones/{id:guid}/sign-out", SignOutMembers).RequireAuthorization(StaffPolicy.Name);
    }

    private static Guid? Person(ClaimsPrincipal user) =>
        !user.HasClaim(c => c.Type == SessionClaims.SupportSession) && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static async Task<IResult> Mine(HttpContext http, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (Person(user) is not { } person) return Results.Ok(Array.Empty<DeviceView>());
        var current = Of((await http.AuthenticateAsync()).Properties);
        return Results.Ok((await ForAsync([person], current, identity, clock.GetUtcNow(), cancellationToken))[person].ToList());
    }

    private static async Task<IResult> SignOutMine(Guid id, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (Person(user) is not { } person) return Results.NotFound();
        return await identity.StaffDevices.SingleOrDefaultAsync(d => d.Id == id && d.PersonId == person, cancellationToken) is { } device
            ? await RevokeAsync(device, user, $"Signed out their {device.Label}", identity, audit, clock, cancellationToken)
            : Results.NotFound();
    }

    /// <summary>An admin signs out the phone of someone on their own staff. Nothing else of that person's is touched.</summary>
    private static async Task<IResult> SignOutMembers(Guid id, ClaimsPrincipal user, PaddocksideIdentityDbContext identity, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (user.FindFirstValue(SessionClaims.Role) != nameof(MemberRole.TenantAdmin))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can sign out someone else's phone.");
        var tenantId = Guid.Parse(user.FindFirstValue(SessionClaims.Tenant)!);
        var device = await identity.StaffDevices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null || !await identity.Memberships.AnyAsync(m => m.PersonId == device.PersonId && m.TenantId == tenantId
            && (m.Role == MemberRole.Viewer || m.Role == MemberRole.Coordinator || m.Role == MemberRole.Manager || m.Role == MemberRole.TenantAdmin), cancellationToken))
            return Results.NotFound();
        var whose = (await identity.Users.AsNoTracking().SingleAsync(p => p.Id == device.PersonId, cancellationToken)).Email;
        return await RevokeAsync(device, user, $"Signed out {whose}'s {device.Label}", identity, audit, clock, cancellationToken);
    }

    private static async Task<IResult> RevokeAsync(StaffDevice device, ClaimsPrincipal user, string what, PaddocksideIdentityDbContext identity, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        device.RevokedAt ??= clock.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(Guid.Parse(user.FindFirstValue(SessionClaims.Tenant)!), "auth.phone-signed-out", what, "Person", device.PersonId, cancellationToken: cancellationToken);
        return Results.NoContent();
    }
}
