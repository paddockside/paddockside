using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    public async Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken, string? statusCallback = null)
    {
        if (!IsConfigured) return SmsSendResult.Failed("Text messages are not configured (Twilio settings are missing).");
        var o = options.Value;

        var form = new Dictionary<string, string> { ["To"] = to, ["From"] = o.FromNumber!, ["Body"] = body };
        if (statusCallback is not null) form["StatusCallback"] = statusCallback;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"2010-04-01/Accounts/{o.AccountSid}/Messages.json") { Content = new FormUrlEncodedContent(form) };
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

/// <summary>
/// Twilio's webhook signature: HMAC-SHA1, keyed with the auth token, over the full URL Twilio called followed by
/// every form field (sorted by name, name then value), Base64. A request without a matching signature did not come
/// from Twilio.
/// </summary>
public static class TwilioSignature
{
    public static string Compute(string authToken, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var data = new StringBuilder(url);
        foreach (var (key, value) in form.OrderBy(f => f.Key, StringComparer.Ordinal)) data.Append(key).Append(value);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
    }

    public static bool IsValid(string? authToken, string url, IEnumerable<KeyValuePair<string, string>> form, string? signature) =>
        !string.IsNullOrEmpty(authToken) && !string.IsNullOrEmpty(signature)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(authToken, url, form)), Encoding.ASCII.GetBytes(signature));
}
