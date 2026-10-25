using HealthSync.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HealthSync.Functions;

public sealed class HealthSyncFunction(HealthSyncService service, ILogger<HealthSyncFunction> logger)
{
    [Function("HealthSyncToDrive")]
    public async Task RunAsync(
        [TimerTrigger("%HEALTH_SYNC_SCHEDULE%", RunOnStartup = false, UseMonitor = true)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(8));
        try
        {
            var counts = await service.RunAsync(cancellationToken: budget.Token);
            logger.LogInformation("Health sync completed; record counts={Counts}", JsonSerializer.Serialize(counts));
        }
        catch (SyncException ex)
        {
            logger.LogError("Health sync failed: {Reason}", ex.Message);
            throw new InvalidOperationException("Health sync failed; see sanitized log above");
        }
    }
}
