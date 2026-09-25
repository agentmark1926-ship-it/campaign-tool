using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>
/// Checks the conditions SPEC Phase 7 asks to alert on and writes one warning per firing condition, starting with "ALERT ".
/// An Azure Monitor log alert (infra/main.bicep) emails the owner when such a line reaches Application Insights.
/// Webhook 5xx, CPU and SQL storage alerts are measured by Azure directly.
/// </summary>
public class AlertMonitor(AppDbContext db, RateLimiter limiter, SettingsService settings, DomainService domains, TimeProvider clock, ILogger<AlertMonitor> log)
{
    public const double FailedShareThreshold = 0.02;
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RecentCampaigns = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var alerts = new List<string>();

        var recent = await db.Campaigns.AsNoTracking()
            .Where(c => c.Recipients > 0 && (c.Status == CampaignStatus.Sending || c.Status == CampaignStatus.Paused || c.CompletedAtUtc > now - RecentCampaigns))
            .ToListAsync(ct);
        foreach (var c in recent.Where(c => (double)c.Failed / c.Recipients > FailedShareThreshold))
            alerts.Add($"Campaign {c.Id} '{c.Name}': {c.Failed} of {c.Recipients} recipients failed ({100.0 * c.Failed / c.Recipients:0.#}%, threshold 2%)");

        var s = await settings.GetAsync(ct);
        var canSend = limiter.Available(now, s.MaxPerMinute, s.MaxPerHour) > 0;
        var domainLeft = await domains.RemainingTodayAsync(s.TimeZone, ct);
        foreach (var c in recent.Where(c => c.Status == CampaignStatus.Sending && (c.LastBatchAtUtc ?? c.StartedAtUtc) < now - StallAfter))
        {
            // Waiting out the hourly limit, a domain's daily limit or a retry backoff is normal; stalled means work is due, sending is allowed, and nothing happened.
            if (domainLeft.TryGetValue(DomainService.DomainOf(c.FromEmail), out var left) && left == 0) continue;
            var due = await db.CampaignRecipients.AnyAsync(r => r.CampaignId == c.Id && r.Status == RecipientStatus.Pending
                && (r.NextAttemptAtUtc == null || r.NextAttemptAtUtc <= now), ct);
            if (due && canSend)
                alerts.Add($"Campaign {c.Id} '{c.Name}' is Sending but has sent no batch for over 30 minutes");
        }

        var unknown = await db.CampaignRecipients.CountAsync(r => r.Status == RecipientStatus.Unknown, ct);
        if (unknown > 0)
            alerts.Add($"{unknown} recipient(s) are Unknown after a restart; review them on the campaign monitor page");

        foreach (var a in alerts) log.LogWarning("ALERT {Alert}", a);
        return alerts;
    }
}
