using Paddockside.Application.Messaging;

namespace Paddockside.Api.Development;

/// <summary>
/// Local development without Twilio: a text is written to the log instead of sent, so the SMS sign-in can be tried
/// on a developer's machine. Never registered outside Development.
/// </summary>
public sealed class DevelopmentSmsLog(ILogger<DevelopmentSmsLog> logger) : ISmsSender
{
    public bool IsConfigured => true;

    public Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken)
    {
        logger.LogWarning("DEVELOPMENT: text to {To} not sent (Twilio is not configured): {Body}", to, body);
        return Task.FromResult(SmsSendResult.Sent($"dev-{Guid.NewGuid():N}"));
    }
}
