using System.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Persistence;
using Paddockside.Infrastructure.Sms;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Twilio calls these: <see cref="StatusPath"/> with what happened to a text we sent (set per text as its status
/// callback), and <see cref="IncomingPath"/> with a text sent to our number (set once on the number in Twilio's
/// console). Twilio signs every request with the account's auth token (X-Twilio-Signature); anything unsigned or
/// wrongly signed is refused.
/// </summary>
public static class TwilioWebhooks
{
    public const string StatusPath = "/api/webhooks/twilio/status";
    public const string IncomingPath = "/api/webhooks/twilio/sms";

    public static void MapTwilioWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost(StatusPath, Status).AllowAnonymous().DisableAntiforgery();
        app.MapPost(IncomingPath, Incoming).AllowAnonymous().DisableAntiforgery();
    }

    /// <summary>Delivered, or failed: recorded on the delivery staff see in the recipient table.</summary>
    private static async Task<IResult> Status(HttpContext http, IOptions<TwilioOptions> twilio, IOptions<EmailOptions> email, TenantScopedDb tenants,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await SignedFormAsync(http, twilio.Value, email.Value, cancellationToken) is not { } form) return Results.Unauthorized();
        var sid = form.GetValueOrDefault("MessageSid");
        if (string.IsNullOrEmpty(sid) || await tenants.DeliveryForProviderIdAsync(sid, cancellationToken) is not { } found) return Results.Ok();

        await using var db = tenants.For(found.TenantId);
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == found.DeliveryId, cancellationToken);
        var now = clock.GetUtcNow();
        switch (form.GetValueOrDefault("MessageStatus"))
        {
            case "delivered":
                delivery.Record(DeliveryStatus.Delivered, now);
                break;
            case "undelivered" or "failed":
                delivery.RecordBounce($"The text was not delivered (Twilio error {form.GetValueOrDefault("ErrorCode") ?? "unknown"}).", now);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok();
    }

    /// <summary>A text to our number. Answered with TwiML: empty, or a STOP/START confirmation.</summary>
    private static async Task<IResult> Incoming(HttpContext http, IOptions<TwilioOptions> twilio, IOptions<EmailOptions> email, SmsInbox inbox,
        CancellationToken cancellationToken)
    {
        if (await SignedFormAsync(http, twilio.Value, email.Value, cancellationToken) is not { } form) return Results.Unauthorized();
        var reply = await inbox.ReceiveAsync(form.GetValueOrDefault("From") ?? "", form.GetValueOrDefault("To") ?? "", form.GetValueOrDefault("Body") ?? "",
            form.GetValueOrDefault("MessageSid") ?? Guid.NewGuid().ToString(), cancellationToken);
        var twiml = reply is null ? "<Response/>" : $"<Response><Message>{SecurityElement.Escape(reply)}</Message></Response>";
        return Results.Content($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{twiml}", "text/xml");
    }

    /// <summary>
    /// The form, if Twilio signed it. The URL signed is the one configured in Twilio: the portal's public address
    /// plus the path, not whatever host name the request reached us on behind Azure's front end.
    /// </summary>
    private static async Task<Dictionary<string, string>?> SignedFormAsync(HttpContext http, TwilioOptions twilio, EmailOptions email, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType) return null;
        var form = (await http.Request.ReadFormAsync(cancellationToken)).ToDictionary(f => f.Key, f => f.Value.ToString());
        var origin = email.PortalBaseUrl is { Length: > 0 } b ? b.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";
        var url = $"{origin}{http.Request.Path}{http.Request.QueryString}";
        return TwilioSignature.IsValid(twilio.AuthToken, url, form, http.Request.Headers["X-Twilio-Signature"]) ? form : null;
    }
}
