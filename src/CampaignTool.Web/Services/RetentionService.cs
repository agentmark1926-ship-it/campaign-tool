using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// Nightly clean-up: raw EmailEvents older than Retention:EventMonths are deleted (campaign counters are permanent),
/// and uploaded import files older than 30 days are deleted from storage.
/// </summary>
public class RetentionService(AppDbContext db, ImportFileStore files, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionService> log)
{
    public static readonly TimeSpan ImportFileAge = TimeSpan.FromDays(30);

    public record Result(int EventsDeleted, int FilesDeleted);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var eventCutoff = now.AddMonths(-Math.Max(1, options.Value.EventMonths));

        var events = 0;
        int batch;
        do
        {
            // Small batches keep the Basic tier's log usage low.
            batch = await db.EmailEvents.Where(e => e.OccurredAtUtc < eventCutoff).OrderBy(e => e.Id).Take(5000).ExecuteDeleteAsync(ct);
            events += batch;
        } while (batch > 0);

        var fileCutoff = now - ImportFileAge;
        var old = await db.Imports.Where(i => i.CreatedAtUtc < fileCutoff && i.BlobPath != "").ToListAsync(ct);
        foreach (var import in old)
        {
            await files.DeleteAsync(import.BlobPath, ct);
            import.BlobPath = "";
        }
        await db.SaveChangesAsync(ct);

        log.LogInformation("Retention: {Events} event(s) older than {Cutoff:yyyy-MM-dd} and {Files} import file(s) deleted", events, eventCutoff, old.Count);
        return new Result(events, old.Count);
    }
}
