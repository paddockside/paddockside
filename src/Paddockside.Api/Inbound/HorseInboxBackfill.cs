using Paddockside.Infrastructure.Inbound;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Inbound;

/// <summary>
/// Once at start-up: gives an inbox address to any horse saved before addresses were issued automatically. New and
/// renamed horses get theirs when they are saved; this only catches up the older ones, then stops.
/// </summary>
public sealed class HorseInboxBackfill(IServiceScopeFactory scopes, TimeProvider clock, ILogger<HorseInboxBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var tenants = scope.ServiceProvider.GetRequiredService<TenantScopedDb>();
            foreach (var tenantId in await tenants.AllTenantIdsAsync(stoppingToken))
            {
                await using var db = tenants.For(tenantId);
                var added = await HorseInboxes.EnsureAsync(db, clock.GetUtcNow(), stoppingToken);
                if (added > 0) logger.LogInformation("Issued {Count} horse inbox addresses for tenant {TenantId}.", added, tenantId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not backfill horse inbox addresses; they are still issued when mail arrives.");
        }
    }
}
