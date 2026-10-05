using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Delivery, open, bounce and spam-complaint events from Postmark, which update the DELIVERY rows
/// (messaging-channels.md §1.5, §2.4).
/// <para>
/// Postmark does not sign webhooks (no HMAC). Its documented protection is HTTP Basic credentials in the webhook
/// URL, so: the credentials are checked in constant time, and an event is applied only when its metadata names a
/// delivery whose stored Postmark message id matches the event's. A forged or replayed event for another
/// delivery, or another tenant, changes nothing.
/// </para>
/// </summary>
public static class PostmarkWebhooks
{
    public const string Path = "/api/webhooks/postmark";

    public static void MapPostmarkWebhooks(this IEndpointRouteBuilder app) =>
        app.MapPost(Path, Handle).AllowAnonymous();

    private static async Task<IResult> Handle(
        HttpContext http,
        IOptions<PostmarkOptions> options,
        TenantScopedDb tenants,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(nameof(PostmarkWebhooks));
        var (username, password) = (options.Value.WebhookUsername, options.Value.WebhookPassword);
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return Results.NotFound();
        if (!HasCredentials(http.Request, username, password))
        {
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"postmark\"";
            return Results.Unauthorized();
        }

        JsonDocument payload;
        try
        {
            payload = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        using (payload)
        {
            var root = payload.RootElement;
            var recordType = Text(root, "RecordType");
            var messageId = Text(root, "MessageID");
            var metadata = root.TryGetProperty("Metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            if (recordType is null || messageId is null
                || !Guid.TryParse(Text(metadata, "tenantId"), out var tenantId)
                || !Guid.TryParse(Text(metadata, "deliveryId"), out var deliveryId))
            {
                // Not one of ours (e.g. Postmark's test payload). Acknowledge so Postmark does not retry.
                return Results.Ok();
            }

            await using var db = tenants.For(tenantId);
            var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.Id == deliveryId && d.ProviderMessageId == messageId, cancellationToken);
            if (delivery is null)
            {
                logger.LogWarning("Postmark {RecordType} for message {MessageId} matched no delivery; ignored.", recordType, messageId);
                return Results.Ok();
            }

            var at = Time(root, "DeliveredAt") ?? Time(root, "ReceivedAt") ?? Time(root, "BouncedAt") ?? clock.GetUtcNow();
            switch (recordType)
            {
                case "Delivery":
                    delivery.Record(DeliveryStatus.Delivered, at);
                    break;

                case "Open":
                    delivery.Record(DeliveryStatus.Opened, at);
                    break;

                case "Bounce":
                    var hard = Text(root, "Type") == "HardBounce"
                               || (root.TryGetProperty("TypeCode", out var code) && code.TryGetInt32(out var c) && c == 1)
                               || (root.TryGetProperty("Inactive", out var inactive) && inactive.ValueKind == JsonValueKind.True);
                    if (!hard) break; // soft and transient bounces are retried by Postmark (§2.4)
                    delivery.RecordBounce($"Bounced: {Text(root, "Description") ?? Text(root, "Name") ?? "the address does not accept mail"}", at);
                    await MarkUndeliverable(db, delivery, at, cancellationToken);
                    break;

                case "SpamComplaint":
                    delivery.Suppress("Marked as spam by the recipient; no more email to this address.", at);
                    await MarkUndeliverable(db, delivery, at, cancellationToken);
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok();
        }
    }

    private static async Task MarkUndeliverable(PaddocksideDbContext db, Delivery delivery, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (delivery.Address is null) return;
        var party = await db.Parties.SingleOrDefaultAsync(p => p.Id == delivery.PartyId, cancellationToken);
        party?.MarkUndeliverable(delivery.Address, at);
    }

    private static bool HasCredentials(HttpRequest request, string username, string password)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(header["Basic ".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes($"{username}:{password}");
        return CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, out var at) ? at : null;
}
