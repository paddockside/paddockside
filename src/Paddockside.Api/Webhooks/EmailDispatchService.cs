using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Sms;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Sends queued email and texts in the background. It makes one pass at start-up (a queued delivery survives a
/// restart), then sleeps until something is queued (the save that queues it signals). While unsent work remains
/// after a failure it tries again after a while, and texts held for quiet hours wake it at seven. It never polls an
/// idle database, so the serverless database can pause.
/// </summary>
public sealed class EmailDispatchService(
    IServiceScopeFactory scopes,
    EmailDispatchSignal signal,
    IOptions<EmailOptions> options,
    IEmailSender sender,
    ISmsSender texts,
    ILogger<EmailDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatchEnabled) return;
        if (!sender.IsConfigured)
            logger.LogWarning("Email is not configured (Postmark:ServerToken is empty): queued email will wait until it is.");
        if (!texts.IsConfigured)
            logger.LogWarning("Texts are not configured (Twilio settings are empty): owners get email instead.");

        var wait = TimeSpan.Zero; // the start-up pass
        while (!stoppingToken.IsCancellationRequested)
        {
            if (wait != TimeSpan.Zero) await signal.WaitAsync(wait, stoppingToken);
            wait = Sooner(await EmailPassAsync(stoppingToken), await TextPassAsync(stoppingToken));
        }
    }

    private static TimeSpan Sooner(TimeSpan a, TimeSpan b) =>
        a == Timeout.InfiniteTimeSpan ? b : b == Timeout.InfiniteTimeSpan ? a : a < b ? a : b;

    /// <summary>Sends what email it can; returns how long to wait before the next pass.</summary>
    private async Task<TimeSpan> EmailPassAsync(CancellationToken stoppingToken)
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

    /// <summary>Sends what texts it can; held texts set the wait to the end of quiet hours.</summary>
    private async Task<TimeSpan> TextPassAsync(CancellationToken stoppingToken)
    {
        if (!texts.IsConfigured) return Timeout.InfiniteTimeSpan;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var pass = await scope.ServiceProvider.GetRequiredService<SmsDispatcher>().DispatchAsync(stoppingToken);
            return pass.Again is { } again ? (again < TimeSpan.Zero ? TimeSpan.Zero : again) : Timeout.InfiniteTimeSpan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Text dispatch failed; it will try again in {Interval}.", SmsDispatcher.RetryInterval);
            return SmsDispatcher.RetryInterval;
        }
    }
}
