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

    /// <summary>Sends <paramref name="body"/> to an E.164 number.</summary>
    Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken);
}

/// <summary>Twilio settings (section "Twilio"); the values come from Key Vault (Twilio--AccountSid and so on).</summary>
public sealed class TwilioOptions
{
    public const string Section = "Twilio";

    public string? AccountSid { get; set; }

    public string? AuthToken { get; set; }

    /// <summary>The number or alphanumeric sender ID texts come from.</summary>
    public string? FromNumber { get; set; }
}
