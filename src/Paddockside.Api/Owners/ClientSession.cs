using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Api.Owners;

/// <summary>
/// Owner sessions (identity-access.md §4.1): 90 days on a device the person has signed in on before, renewed while
/// they use it; a day on a device we have not seen them on, which then becomes known. The session carries the
/// tenant and the party the person acts as there; the tenant filter does the rest.
/// </summary>
public static class ClientSession
{
    public static readonly TimeSpan KnownDeviceLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan NewDeviceLifetime = TimeSpan.FromDays(1);

    /// <summary>Which people have signed in on this browser: protected, so it cannot be forged or read.</summary>
    public const string DeviceCookie = "__Host-paddockside-device";

    /// <summary>The nonce that ties a code to the browser that asked for it (30 minutes).</summary>
    public const string BindingCookie = "__Host-paddockside-signin";

    private const string DevicePurpose = "Paddockside.Owners.KnownDevice";
    private const int RememberedPeople = 8;

    public static async Task SignInAsync(HttpContext http, Person person, Guid tenantId, Guid partyId, string method)
    {
        var claimTypes = http.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme, claimTypes.UserNameClaimType, claimTypes.RoleClaimType);
        identity.AddClaim(new Claim(claimTypes.UserIdClaimType, person.Id.ToString()));
        identity.AddClaim(new Claim(claimTypes.UserNameClaimType, person.UserName!));
        if (person.Email is { } email) identity.AddClaim(new Claim(claimTypes.EmailClaimType, email));
        identity.AddClaim(new Claim(claimTypes.SecurityStampClaimType, person.SecurityStamp!));
        identity.AddClaim(new Claim(SessionClaims.Tenant, tenantId.ToString()));
        identity.AddClaim(new Claim(SessionClaims.Role, nameof(MemberRole.Owner)));
        identity.AddClaim(new Claim(SessionClaims.AudienceClass, nameof(AudienceClass.Client)));
        identity.AddClaim(new Claim(SessionClaims.Party, partyId.ToString()));
        identity.AddClaim(new Claim("amr", method));

        var known = KnownPeople(http).Contains(person.Id);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            ExpiresUtc = DateTimeOffset.UtcNow + (known ? KnownDeviceLifetime : NewDeviceLifetime),
        };
        await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await http.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity), properties);
        RememberDevice(http, person.Id);
    }

    /// <summary>The browser's sign-in nonce, created if it has none yet.</summary>
    public static string EnsureBinding(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(BindingCookie, out var existing) && existing.Length >= 32) return existing;
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(BindingCookie, nonce, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromMinutes(30),
        });
        return nonce;
    }

    public static string? Binding(HttpContext http) => http.Request.Cookies.TryGetValue(BindingCookie, out var value) ? value : null;

    private static HashSet<Guid> KnownPeople(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(DeviceCookie, out var value)) return [];
        try
        {
            var plain = Protector(http).Unprotect(value);
            return plain.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToHashSet();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return []; // tampered with, or keys rotated: treat the device as new
        }
    }

    private static void RememberDevice(HttpContext http, Guid personId)
    {
        var people = KnownPeople(http).Where(p => p != personId).Prepend(personId).Take(RememberedPeople);
        http.Response.Cookies.Append(DeviceCookie, Protector(http).Protect(string.Join(',', people)), new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(400),
        });
    }

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(DevicePurpose);
}

/// <summary>Owner endpoints: a signed-in client acting as a party in the active tenant.</summary>
public static class ClientPolicy
{
    public const string Name = "Client";

    public static void Build(AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireClaim(SessionClaims.Tenant)
        .RequireClaim(SessionClaims.Party)
        .RequireClaim(SessionClaims.AudienceClass, nameof(AudienceClass.Client));
}

/// <summary>
/// Every minute the session is checked against the person (their security stamp) and its claims rebuilt. The
/// rebuild knows only staff memberships, so this carries over what it cannot know: how the person signed in
/// ("amr", which the staff policy needs) and, for owners, the tenant and party they are acting as.
/// </summary>
public static class SessionRefresh
{
    private static readonly string[] OwnerClaims = [SessionClaims.Tenant, SessionClaims.Role, SessionClaims.AudienceClass, SessionClaims.Party];

    /// <summary>An operator inside a tenant on a support session stays there (as a Viewer) until they leave or it ends.</summary>
    private static readonly string[] SupportClaims = [SessionClaims.Tenant, SessionClaims.Role, SessionClaims.AudienceClass, SessionClaims.SupportSession];

    public static void Configure(SecurityStampValidatorOptions options)
    {
        // Checked every minute, so a suspended member loses access within a minute (identity-access.md §6).
        options.ValidationInterval = TimeSpan.FromMinutes(1);
        options.OnRefreshingPrincipal = Refresh;
    }

    private static Task Refresh(SecurityStampRefreshingPrincipalContext context)
    {
        if (context.CurrentPrincipal?.Identity is not ClaimsIdentity current || context.NewPrincipal?.Identity is not ClaimsIdentity fresh)
            return Task.CompletedTask;

        foreach (var amr in current.FindAll("amr").Where(c => !fresh.HasClaim("amr", c.Value)))
            fresh.AddClaim(new Claim("amr", amr.Value));

        var keep = current.HasClaim(c => c.Type == SessionClaims.SupportSession) ? SupportClaims
            : current.FindFirst(SessionClaims.AudienceClass)?.Value == nameof(AudienceClass.Client) ? OwnerClaims
            : [];
        foreach (var type in keep)
        {
            foreach (var stale in fresh.FindAll(type).ToList()) fresh.RemoveClaim(stale);
            foreach (var kept in current.FindAll(type)) fresh.AddClaim(new Claim(type, kept.Value));
        }

        return Task.CompletedTask;
    }
}
