using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Workers;

/// <summary>
/// The one in-process worker: recovers stale claims, starts due scheduled campaigns, runs queued contact imports,
/// and sends campaign batches within the rate limit. Idle ticks are 15 seconds; while sending it runs as fast as the limit allows.
/// </summary>
public class CampaignWorker(IServiceScopeFactory scopes, RateLimiter limiter, TimeProvider clock, ILogger<CampaignWorker> log) : BackgroundService
{
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ImportTick = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedLimiterAsync(stoppingToken);
        var nextSchedulerRun = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = ImportTick;
            try
            {
                using var scope = scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<CampaignSender>();
                var now = clock.GetUtcNow().UtcDateTime;
                if (now >= nextSchedulerRun)
                {
                    await sender.RecoverStaleClaimsAsync(stoppingToken);
                    await sender.StartDueScheduledAsync(stoppingToken);
                    nextSchedulerRun = now + IdleTick;
                }

                var imports = scope.ServiceProvider.GetRequiredService<ContactImportService>();
                while (await imports.ProcessNextAsync(stoppingToken)) { }

                var next = await sender.SendBatchAsync(stoppingToken);
                if (next is { } due)
                {
                    var untilDue = due - clock.GetUtcNow().UtcDateTime;
                    wait = untilDue <= TimeSpan.Zero ? TimeSpan.Zero : untilDue < ImportTick ? untilDue : ImportTick;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Campaign worker tick failed");
            }
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, clock, stoppingToken).ContinueWith(_ => { });
        }
    }

    /// <summary>A restart must not reset the hourly window: seed it with the last hour's sends.</summary>
    private async Task SeedLimiterAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var since = clock.GetUtcNow().UtcDateTime.AddHours(-1);
            limiter.Seed(await db.CampaignRecipients.Where(r => r.SentAtUtc > since).Select(r => r.SentAtUtc!.Value).ToListAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not seed the rate limiter");
        }
    }
}
