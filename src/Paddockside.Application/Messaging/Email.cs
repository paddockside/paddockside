namespace Paddockside.Application.Messaging;

/// <summary>One email to one recipient, fully rendered.</summary>
/// <param name="Metadata">Echoed back by the provider's webhooks, so events find their delivery directly.</param>
public sealed record OutboundEmail(
    string FromAddress,
    string FromName,
    string ToAddress,
    string ToName,
    string ReplyTo,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What the provider said. A permanent failure will never succeed on retry (e.g. an inactive address).</summary>
public sealed record EmailSendResult(bool Accepted, string? ProviderMessageId, bool PermanentFailure, string? Error)
{
    public static EmailSendResult Sent(string providerMessageId) => new(true, providerMessageId, false, null);

    public static EmailSendResult Failed(string error, bool permanent) => new(false, null, permanent, error);
}

/// <summary>Sends email through the provider (Postmark). Never throws for provider errors; reports them.</summary>
public interface IEmailSender
{
    /// <summary>False when no provider credentials are configured: nothing can be sent.</summary>
    bool IsConfigured { get; }

    Task<EmailSendResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken);
}

/// <summary>Sending identity and reply routing (messaging-channels.md §2.1–§2.2). Section "Email".</summary>
public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>The From address; the tenant's name is the display name.</summary>
    public string FromAddress { get; set; } = "updates@mail.paddockside.com.au";

    /// <summary>Replies go to <c>r-{token}@{tenant slug}.{InboundDomain}</c>.</summary>
    public string InboundDomain { get; set; } = "in.paddockside.com.au";

    /// <summary>
    /// Where the owner portal lives, for links in emails ("{PortalBaseUrl}/my/link#token"). No links are added when
    /// unset.
    /// </summary>
    public string? PortalBaseUrl { get; set; }

    /// <summary>The background sender drains queued email when true (switched off in tests).</summary>
    public bool DispatchEnabled { get; set; } = true;

    /// <summary>How soon the background sender tries again while email it could not send is still queued.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Wakes the background sender when email is queued. The sender never polls: the database is serverless and
/// pauses when idle, and a query every few seconds would keep it running (and billing) around the clock.
/// </summary>
public sealed class EmailDispatchSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the next pass picks this up too.
        }
    }

    /// <summary>True when signalled, false when the timeout passed first.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}
