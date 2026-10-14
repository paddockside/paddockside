namespace Paddockside.Application.Messaging;

public sealed record SmsSendResult(bool Accepted, string? ProviderMessageId, string? Error)
{
    public static SmsSendResult Sent(string id) => new(true, id, null);

    public static SmsSendResult Failed(string error) => new(false, null, error);
}

/// <summary>Sends a text message (messaging-channels.md §4). Twilio in Azure.</summary>
public interface ISmsSender
{
    bool IsConfigured { get; }

    /// <summary>
    /// Sends <paramref name="body"/> to an E.164 number. <paramref name="statusCallback"/> is where the provider
    /// reports delivered or failed; without one, nothing is heard after sending.
    /// </summary>
    Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken, string? statusCallback = null);
}

/// <summary>Twilio settings (section "Twilio"); the values come from Key Vault (Twilio--AccountSid and so on).</summary>
public sealed class TwilioOptions
{
    public const string Section = "Twilio";

    public string? AccountSid { get; set; }

    public string? AuthToken { get; set; }

    /// <summary>
    /// The number texts come from and replies go to: a long number that can receive (P20). One for the product
    /// until each tenant has its own.
    /// </summary>
    public string? FromNumber { get; set; }

    /// <summary>
    /// Twilio's region: empty or "us1" for the United States (the default), "au1" for Australia, "ie1" for Ireland.
    /// An account set up in a region has its own credentials there, so this must match the account, not the number.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>Where the API is for <see cref="Region"/>.</summary>
    public Uri ApiBaseAddress => (Region?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "us1" => new Uri("https://api.twilio.com/"),
        "au1" => new Uri("https://api.sydney.au1.twilio.com/"),
        "ie1" => new Uri("https://api.dublin.ie1.twilio.com/"),
        var other => throw new InvalidOperationException($"Unknown Twilio region '{other}': use us1, au1 or ie1."),
    };
}
