using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Security;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using QRCoder;

namespace Paddockside.Api.Auth;

/// <summary>
/// Staff sign-in, in steps (identity-access.md §4.2):
/// <list type="number">
/// <item><c>POST /api/auth/password</c> — email and password. Never signs in; on success it sets a short-lived
/// "second factor pending" cookie and says what comes next.</item>
/// <item>Either <c>POST /api/auth/totp</c> (or <c>/recovery-code</c>) for someone already enrolled, or
/// <c>GET</c> then <c>POST /api/auth/enrolment</c> for someone who is not: the second factor is mandatory, so
/// enrolment happens before the first session exists.</item>
/// </list>
/// Every failure says the same thing, so responses do not reveal which accounts exist.
/// </summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "sign-in";
    private const string Issuer = "Paddockside";
    private const int RecoveryCodeCount = 10;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/password", SignInWithPassword);
        auth.MapPost("/totp", SignInWithAuthenticatorCode);
        auth.MapPost("/recovery-code", SignInWithRecoveryCode);
        auth.MapGet("/enrolment", StartEnrolment);
        auth.MapPost("/enrolment", CompleteEnrolment);
        auth.MapPost("/sign-out", (Delegate)SignOut);
        app.MapGet("/api/auth/me", Me).RequireAuthorization(StaffPolicy.Name);
    }

    public sealed record PasswordRequest(string Email, string Password);

    public sealed record CodeRequest(string Code);

    /// <summary>What the client must do next: "totp" (enter a code) or "enrol" (set up an authenticator).</summary>
    public sealed record NextStep(string Next);

    public sealed record EnrolmentDetails(string SharedKey, string AuthenticatorUri, string QrCodePng);

    public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

    public sealed record SessionInfo(string Email, string TenantName, string Role);

    private static IResult SignInFailed() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "That did not match. Check your details and try again.");

    private static IResult LockedOut() =>
        Results.Problem(statusCode: StatusCodes.Status423Locked, title: "Too many attempts. Wait 15 minutes, then try again.");

    private static async Task<IResult> SignInWithPassword(
        PasswordRequest request,
        HttpContext http,
        UserManager<Person> users,
        SignInManager<Person> signIn,
        PaddocksideIdentityDbContext identity)
    {
        var person = await users.FindByEmailAsync(request.Email.Trim());
        if (person is null) return SignInFailed();

        var result = await signIn.CheckPasswordSignInAsync(person, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut) return LockedOut();
        if (!result.Succeeded) return SignInFailed();

        // Password sign-in is for staff only; owners sign in without a password (§4.1).
        if (await identity.ActiveStaffMembershipAsync(person.Id) is null) return SignInFailed();

        await http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, PendingSecondFactor(person));
        return Results.Ok(new NextStep(person.TwoFactorEnabled ? "totp" : "enrol"));
    }

    private static async Task<IResult> SignInWithAuthenticatorCode(CodeRequest request, SignInManager<Person> signIn)
    {
        var person = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (person is null || !person.TwoFactorEnabled) return SignInFailed();

        var result = await signIn.TwoFactorAuthenticatorSignInAsync(Normalise(request.Code), isPersistent: true, rememberClient: false);
        return result.Succeeded ? Results.NoContent() : result.IsLockedOut ? LockedOut() : SignInFailed();
    }

    private static async Task<IResult> SignInWithRecoveryCode(CodeRequest request, SignInManager<Person> signIn)
    {
        var person = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (person is null || !person.TwoFactorEnabled) return SignInFailed();

        var result = await signIn.TwoFactorRecoveryCodeSignInAsync(request.Code.Replace(" ", string.Empty));
        return result.Succeeded ? Results.NoContent() : result.IsLockedOut ? LockedOut() : SignInFailed();
    }

    /// <summary>
    /// Shows the authenticator key — only to someone who has just passed the password step and has never
    /// enrolled. An enrolled person's key is never shown again, so a stolen password cannot re-read it.
    /// </summary>
    private static async Task<IResult> StartEnrolment(UserManager<Person> users, SignInManager<Person> signIn)
    {
        var person = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (person is null) return Results.Unauthorized();
        if (person.TwoFactorEnabled) return Results.Conflict();

        var key = await users.GetAuthenticatorKeyAsync(person);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(person);
            key = await users.GetAuthenticatorKeyAsync(person) ?? throw new InvalidOperationException("No authenticator key was generated.");
        }

        var uri = $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(person.Email!)}"
                  + $"?secret={key}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6";
        using var qr = new QRCodeGenerator().CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = Convert.ToBase64String(new PngByteQRCode(qr).GetGraphic(8));

        return Results.Ok(new EnrolmentDetails(Grouped(key), uri, png));
    }

    private static async Task<IResult> CompleteEnrolment(
        CodeRequest request,
        HttpContext http,
        UserManager<Person> users,
        SignInManager<Person> signIn)
    {
        var person = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (person is null) return Results.Unauthorized();
        if (person.TwoFactorEnabled) return Results.Conflict();

        var valid = await users.VerifyTwoFactorTokenAsync(person, users.Options.Tokens.AuthenticatorTokenProvider, Normalise(request.Code));
        if (!valid) return SignInFailed();

        await users.SetTwoFactorEnabledAsync(person, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(person, RecoveryCodeCount) ?? [];

        await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await signIn.SignInWithClaimsAsync(person, isPersistent: true, [new Claim("amr", "mfa")]);
        return Results.Ok(new RecoveryCodes(codes.ToList()));
    }

    private static async Task<IResult> SignOut(HttpContext http)
    {
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal user, PaddocksideDbContext db)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync();
        if (tenant is null) return Results.Forbid();

        return Results.Ok(new SessionInfo(
            user.FindFirstValue(ClaimTypes.Email) ?? user.Identity!.Name!,
            tenant.Name,
            user.FindFirstValue(SessionClaims.Role)!));
    }

    /// <summary>The same principal SignInManager uses for "password checked, second factor pending".</summary>
    internal static ClaimsPrincipal PendingSecondFactor(Person person) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, person.Id.ToString())], IdentityConstants.TwoFactorUserIdScheme));

    private static string Normalise(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);

    private static string Grouped(string key) =>
        string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLowerInvariant();
}
