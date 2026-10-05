using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Drains queued email in the background: every few seconds it runs the <see cref="EmailDispatcher"/>. A queued
/// delivery survives a restart; it is simply sent on the next pass.
/// </summary>
public sealed class EmailDispatchService(IServiceScopeFactory scopes, IOptions<EmailOptions> options, IEmailSender sender, ILogger<EmailDispatchService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatchEnabled) return;
        if (!sender.IsConfigured)
            logger.LogWarning("Email is not configured (Postmark:ServerToken is empty): queued email will wait until it is.");

        using var timer = new PeriodicTimer(options.Value.DispatchInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Email dispatch failed; it will try again on the next pass.");
            }
        }
    }
}
