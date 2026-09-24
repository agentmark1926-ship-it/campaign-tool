using CampaignTool.Web.Services;

namespace CampaignTool.Web.Workers;

/// <summary>Runs the retention job and the alert checks. Retention runs nightly at 3 AM in App:TimeZone; alert checks every 5 minutes.</summary>
public class RetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RetentionWorker> log) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? lastRetentionDay = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AlertMonitor>().CheckAsync(stoppingToken);

                var tz = (await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync(stoppingToken)).TimeZone;
                var local = TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone)
                    ? TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, zone)
                    : clock.GetUtcNow().UtcDateTime;
                var today = DateOnly.FromDateTime(local);
                if (local.Hour >= 3 && lastRetentionDay != today)
                {
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(stoppingToken);
                    lastRetentionDay = today;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Retention / alert check failed");
            }
            await Task.Delay(CheckEvery, clock, stoppingToken).ContinueWith(_ => { });
        }
    }
}
