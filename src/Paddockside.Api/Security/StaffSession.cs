using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Api.Security;

/// <summary>Staff session lifetime (identity-access.md §4.2): 12 hours idle, 30 days absolute, per device.</summary>
public static class StaffSessionCookie
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);
    private const string AbsoluteExpiryKey = "paddockside.absolute-expiry";

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "__Host-paddockside";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = IdleTimeout;
        options.SlidingExpiration = true;

        // An API answers with status codes, never redirects to a sign-in page.
        options.Events.OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden);

        // Sliding renewal keeps the session alive while in use, but never past 30 days from sign-in.
        options.Events.OnSigningIn = context =>
        {
            // Owner sessions are long-lived by design (identity-access.md §4.1): no absolute cap, only their own expiry.
            var isClient = context.Principal?.FindFirst(SessionClaims.AudienceClass)?.Value == nameof(AudienceClass.Client);
            if (!isClient && !context.Properties.Items.ContainsKey(AbsoluteExpiryKey))
                context.Properties.Items[AbsoluteExpiryKey] = DateTimeOffset.UtcNow.Add(AbsoluteLifetime).ToString("O");
            return Task.CompletedTask;
        };
        var validateSecurityStamp = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (context.Properties.Items.TryGetValue(AbsoluteExpiryKey, out var value)
                && DateTimeOffset.TryParse(value, out var expiry)
                && expiry <= DateTimeOffset.UtcNow)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                return;
            }

            await validateSecurityStamp(context);
        };
    }

    /// <summary>The short-lived cookie that carries "password checked, second factor pending".</summary>
    public static void ConfigurePending(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "__Host-paddockside-pending";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    }

    private static Task Status(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Staff endpoints need a full sign-in: a session that passed the second factor ("amr" = "mfa"), carries an
/// active tenant, and belongs to the Staff class. A password alone never satisfies it.
/// </summary>
public static class StaffPolicy
{
    public const string Name = "Staff";

    public static void Build(AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireClaim("amr", "mfa")
        .RequireClaim(SessionClaims.Tenant)
        .RequireClaim(SessionClaims.AudienceClass, nameof(AudienceClass.Staff));
}

/// <summary>
/// The operator console (identity-access.md §7): a full sign-in (second factor passed) by a person flagged as an
/// operator. Tenant-free: operator endpoints read tenants' metadata only, never their data.
/// </summary>
public static class OperatorPolicy
{
    public const string Name = "Operator";

    public static void Build(AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireClaim("amr", "mfa")
        .RequireClaim(SessionClaims.Operator, "true");
}

/// <summary>
/// Cross-site request forgery guard for the JSON API: state-changing requests must carry a custom header,
/// which a browser will not send cross-origin without a CORS preflight this API never grants. Works with the
/// SameSite=Strict cookie as a second layer (non-functional.md §2).
/// </summary>
public sealed class RequireApiRequestHeader(RequestDelegate next)
{
    public const string HeaderName = "X-Paddockside-Request";

    public Task InvokeAsync(HttpContext context)
    {
        var isUnsafe = !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method));
        // Webhooks come from providers, not browsers; they authenticate themselves instead (PostmarkWebhooks).
        var isWebhook = context.Request.Path.StartsWithSegments("/api/webhooks");
        if (isUnsafe && !isWebhook && context.Request.Path.StartsWithSegments("/api") && !context.Request.Headers.ContainsKey(HeaderName))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
