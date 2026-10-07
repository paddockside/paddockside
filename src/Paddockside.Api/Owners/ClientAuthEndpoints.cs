using Paddockside.Api.Auth;
using Paddockside.Infrastructure.ClientAccess;

namespace Paddockside.Api.Owners;

/// <summary>
/// Owner sign-in (identity-access.md §4.1). Asking for a link or code always answers the same way, known address
/// or not. A link is redeemed by the page it opens posting it here, never by the link being fetched, so a mail
/// scanner that "clicks" every link cannot use it up.
/// </summary>
public static class ClientAuthEndpoints
{
    public sealed record EmailRequest(string Email, string? ReturnPath);

    public sealed record SmsRequest(string Mobile, string? ReturnPath);

    public sealed record CodeRequest(string Code);

    public sealed record LinkRequest(string Token);

    public sealed record Signed(string Destination);

    public static void MapClientAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/client-auth").AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        auth.MapPost("/email", RequestEmail);
        auth.MapPost("/sms", RequestSms);
        auth.MapPost("/code", Code);
        auth.MapPost("/link", Link);
    }

    private static async Task<IResult> RequestEmail(EmailRequest request, HttpContext http, ClientSignIn signIn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type the email address you get updates at.");
        await signIn.RequestEmailLinkAsync(request.Email, ClientSession.EnsureBinding(http), request.ReturnPath, cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> RequestSms(SmsRequest request, HttpContext http, ClientSignIn signIn, CancellationToken cancellationToken)
    {
        var binding = ClientSession.EnsureBinding(http);
        return await signIn.RequestSmsCodeAsync(request.Mobile ?? string.Empty, binding, request.ReturnPath, cancellationToken)
            ? Results.Accepted()
            : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That doesn't look like a mobile number. Try it as 0412 345 678.");
    }

    private static async Task<IResult> Code(CodeRequest request, HttpContext http, ClientSignIn signIn, CancellationToken cancellationToken)
    {
        if (ClientSession.Binding(http) is not { } binding)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Ask for a code on this screen first, then type it here.");
        return await FinishAsync(http, await signIn.RedeemCodeAsync(request.Code ?? string.Empty, binding, cancellationToken), "otp", StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> Link(LinkRequest request, HttpContext http, ClientSignIn signIn, CancellationToken cancellationToken) =>
        await FinishAsync(http, await signIn.RedeemLinkAsync(request.Token ?? string.Empty, cancellationToken), "email", StatusCodes.Status410Gone);

    private static async Task<IResult> FinishAsync(HttpContext http, OneOf outcome, string method, int failureStatus)
    {
        if (outcome.Failure is { } failure)
            return Results.Problem(statusCode: failureStatus, title: failure.Message, extensions: new Dictionary<string, object?> { ["returnPath"] = failure.ReturnPath });

        var success = outcome.Success!;
        await ClientSession.SignInAsync(http, success.Person, success.TenantId, success.PartyId, method);
        await http.RequestServices.GetRequiredService<Audit.AuditLog>().RecordAsync(success.TenantId, "auth.owner-signed-in",
            method == "otp" ? "Owner signed in with a code" : "Owner signed in with an emailed link", "Party", success.PartyId,
            actor: (success.Person.Id, success.Person.Email ?? success.Person.PhoneNumber ?? "Owner"));
        return Results.Ok(new Signed(success.Destination));
    }
}
