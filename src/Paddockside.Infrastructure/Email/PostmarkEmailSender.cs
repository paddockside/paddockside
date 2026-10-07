using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;

namespace Paddockside.Infrastructure.Email;

/// <summary>
/// Postmark settings, section "Postmark". In Azure the values come from Key Vault (secrets
/// <c>Postmark--ServerToken</c>, <c>Postmark--WebhookUsername</c>, <c>Postmark--WebhookPassword</c>).
/// Locally the server token may be Postmark's test token <c>POSTMARK_API_TEST</c>, which exercises the API without
/// delivering anything.
/// </summary>
public sealed class PostmarkOptions
{
    public const string Section = "Postmark";

    public string? ServerToken { get; set; }

    /// <summary>Postmark has no webhook signatures; it sends these as HTTP Basic credentials in the webhook URL.</summary>
    public string? WebhookUsername { get; set; }

    public string? WebhookPassword { get; set; }

    /// <summary>The transactional stream.</summary>
    public string MessageStream { get; set; } = "outbound";
}

/// <summary>Sends through Postmark's single-email API. Reports provider errors rather than throwing.</summary>
public sealed class PostmarkEmailSender(HttpClient http, IOptions<PostmarkOptions> options, ILogger<PostmarkEmailSender> logger) : IEmailSender
{
    // Postmark error codes that will never succeed on retry for this recipient.
    private static readonly HashSet<int> PermanentRecipientErrors =
    [
        300, // invalid email request (e.g. a malformed address)
        406, // inactive recipient: Postmark suppressed the address after a hard bounce or complaint
    ];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ServerToken);

    public async Task<EmailSendResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return EmailSendResult.Failed("Email is not configured (no Postmark server token).", permanent: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "email")
        {
            Content = JsonContent.Create(new PostmarkEmail(
                Mailbox(email.FromName, email.FromAddress),
                Mailbox(email.ToName, email.ToAddress),
                // A reply address that can never exist (the demo's .test domain) is left off: filters treat it as forgery.
                EmailAddresses.CanReceive(email.ReplyTo) ? email.ReplyTo : null,
                email.Subject,
                email.HtmlBody,
                email.TextBody,
                options.Value.MessageStream,
                TrackOpens: true,
                email.Metadata)),
        };
        request.Headers.Add("X-Postmark-Server-Token", options.Value.ServerToken);
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<PostmarkResponse>(cancellationToken);

            if (response.IsSuccessStatusCode && body is { ErrorCode: 0, MessageID: { Length: > 0 } id })
                return EmailSendResult.Sent(id);

            var error = $"Postmark {(int)response.StatusCode}, code {body?.ErrorCode}: {body?.Message}";
            var permanent = response.StatusCode == HttpStatusCode.UnprocessableEntity && body is not null && PermanentRecipientErrors.Contains(body.ErrorCode);
            if (!permanent) logger.LogWarning("Email not accepted; will retry. {Error}", error);
            return EmailSendResult.Failed(error, permanent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not reach Postmark; will retry.");
            return EmailSendResult.Failed($"Could not reach Postmark: {ex.Message}", permanent: false);
        }
    }

    /// <summary>"Display Name" &lt;address&gt;, with the name quoted so commas and brackets are safe.</summary>
    private static string Mailbox(string name, string address) =>
        string.IsNullOrWhiteSpace(name) ? address : $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" <{address}>";

    private sealed record PostmarkEmail(
        string From,
        string To,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyTo,
        string Subject,
        string HtmlBody,
        string TextBody,
        string MessageStream,
        bool TrackOpens,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed record PostmarkResponse(
        [property: JsonPropertyName("ErrorCode")] int ErrorCode,
        [property: JsonPropertyName("Message")] string? Message,
        [property: JsonPropertyName("MessageID")] string? MessageID);
}
