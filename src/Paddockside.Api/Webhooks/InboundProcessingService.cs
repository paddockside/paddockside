using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Inbound;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Webhooks;

/// <summary>
/// Processes stored inbound email off the queue. It polls the queue, not the database: the database is only
/// touched when there is mail, so the serverless database can still pause. A message that keeps failing is left
/// in Received after five tries (still stored, visible to staff) rather than retried for ever.
/// </summary>
public sealed class InboundProcessingService(IServiceScopeFactory scopes, IInboundQueue queue, ILogger<InboundProcessingService> logger)
    : BackgroundService
{
    private const int MaxTries = 5;
    private static readonly TimeSpan Fastest = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Slowest = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnprocessedAsync(stoppingToken);

        var wait = Fastest;
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<QueuedInbound> batch;
            try
            {
                batch = await queue.ReceiveAsync(wait, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not read the inbound queue; trying again shortly.");
                await Task.Delay(Slowest, stoppingToken);
                continue;
            }

            // Quiet: back off towards one look every 30 seconds. Busy: straight back.
            wait = batch.Count == 0 ? TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, Slowest.Ticks)) : Fastest;
            foreach (var message in batch) await ProcessAsync(message, stoppingToken);
        }
    }

    private async Task ProcessAsync(QueuedInbound message, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<InboundProcessor>().ProcessAsync(message.TenantId, message.InboundMessageId, stoppingToken);
            await queue.CompleteAsync(message, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (message.DequeueCount >= MaxTries)
            {
                logger.LogError(ex, "Inbound message {Id} failed {Tries} times; left unprocessed.", message.InboundMessageId, message.DequeueCount);
                await queue.CompleteAsync(message, stoppingToken);
            }
            else
            {
                logger.LogWarning(ex, "Inbound message {Id} failed; the queue will offer it again.", message.InboundMessageId);
            }
        }
    }

    /// <summary>Anything stored but not processed when the app last stopped (the local queue is in memory).</summary>
    private async Task RequeueUnprocessedAsync(CancellationToken stoppingToken)
    {
        if (queue is not MemoryInboundQueue) return; // the Storage Queue keeps its own messages across restarts
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var tenants = scope.ServiceProvider.GetRequiredService<TenantScopedDb>();
            foreach (var (tenantId, id) in await tenants.UnprocessedInboundAsync(500, stoppingToken))
                await queue.EnqueueAsync(tenantId, id, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not re-queue unprocessed inbound mail at start-up.");
        }
    }
}
