using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Sends queued email in the background. It makes one pass at start-up (a queued delivery survives a restart),
/// then sleeps until the compose endpoint signals new email. While unsent email remains after a failure, it
/// tries again every <see cref="EmailOptions.RetryInterval"/>. It never polls an idle database, so the
/// serverless database can pause.
/// </summary>
public sealed class EmailDispatchService(
    IServiceScopeFactory scopes,
    EmailDispatchSignal signal,
    IOptions<EmailOptions> options,
    IEmailSender sender,
    ILogger<EmailDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatchEnabled) return;
        if (!sender.IsConfigured)
            logger.LogWarning("Email is not configured (Postmark:ServerToken is empty): queued email will wait until it is.");

        var wait = TimeSpan.Zero; // the start-up pass
        while (!stoppingToken.IsCancellationRequested)
        {
            if (wait != TimeSpan.Zero) await signal.WaitAsync(wait, stoppingToken);
            wait = await PassAsync(stoppingToken);
        }
    }

    /// <summary>Sends what it can; returns how long to wait before the next pass.</summary>
    private async Task<TimeSpan> PassAsync(CancellationToken stoppingToken)
    {
        if (!sender.IsConfigured) return Timeout.InfiniteTimeSpan;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<EmailDispatcher>();
            var accepted = await dispatcher.DispatchAsync(stoppingToken);
            if (!await dispatcher.HasQueuedAsync(stoppingToken)) return Timeout.InfiniteTimeSpan;
            return accepted > 0 ? TimeSpan.Zero : options.Value.RetryInterval; // more batches now; failures later
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Email dispatch failed; it will try again in {Interval}.", options.Value.RetryInterval);
            return options.Value.RetryInterval;
        }
    }
}
