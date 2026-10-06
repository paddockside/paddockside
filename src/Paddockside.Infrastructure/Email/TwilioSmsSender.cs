using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;

namespace Paddockside.Infrastructure.Email;

/// <summary>Twilio's Messages API over plain HTTP: one POST per text, no SDK.</summary>
public sealed class TwilioSmsSender(HttpClient http, IOptions<TwilioOptions> options, ILogger<TwilioSmsSender> logger) : ISmsSender
{
    public bool IsConfigured =>
        !string.IsNullOrEmpty(options.Value.AccountSid) && !string.IsNullOrEmpty(options.Value.AuthToken) && !string.IsNullOrEmpty(options.Value.FromNumber);

    public async Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return SmsSendResult.Failed("Text messages are not configured (Twilio settings are missing).");
        var o = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{o.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = to, ["From"] = o.FromNumber!, ["Body"] = body }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{o.AccountSid}:{o.AuthToken}")));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (response.IsSuccessStatusCode && document.RootElement.TryGetProperty("sid", out var sid))
                return SmsSendResult.Sent(sid.GetString()!);

            var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            logger.LogWarning("Twilio refused a text: {Status} {Message}", (int)response.StatusCode, message);
            return SmsSendResult.Failed($"Twilio {(int)response.StatusCode}: {message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not reach Twilio.");
            return SmsSendResult.Failed("Could not reach the text message service.");
        }
    }
}
