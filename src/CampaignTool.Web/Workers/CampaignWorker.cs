using CampaignTool.Web.Services;

namespace CampaignTool.Web.Workers;

/// <summary>
/// The one in-process worker. Each tick it processes queued contact imports; campaign sending is added in Phase 5.
/// </summary>
public class CampaignWorker(IServiceScopeFactory scopes, ILogger<CampaignWorker> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var imports = scope.ServiceProvider.GetRequiredService<ContactImportService>();
                while (await imports.ProcessNextAsync(stoppingToken)) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Campaign worker tick failed");
            }
            await Task.Delay(Tick, stoppingToken).ContinueWith(_ => { });
        }
    }
}
