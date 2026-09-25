using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>Figures for the dashboard: 30-day totals with the previous 30 days for comparison, and one value per day for the charts.</summary>
public class DashboardService(AppDbContext db, TimeProvider clock)
{
    public const int Days = 30;

    public record Series(int[] Daily, int Total, int PreviousTotal);

    public record Summary(
        DateTime FromUtc, DateTime ToUtc,
        Series Sent, Series Delivered, Series Clicks, Series Bounced,
        int UniqueClickers, int SentLastHour, DateTime? LastReportUtc, int Subscribed);

    public async Task<Summary> GetAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = now.Date;
        var from = today.AddDays(-(Days - 1));
        var previousFrom = from.AddDays(-Days);

        var sent = await DailyAsync(db.CampaignRecipients.Where(r => r.SentAtUtc >= previousFrom).Select(r => r.SentAtUtc!.Value), ct);
        var delivered = await DailyAsync(db.CampaignRecipients.Where(r => r.DeliveredAtUtc >= previousFrom).Select(r => r.DeliveredAtUtc!.Value), ct);
        var clicks = await DailyAsync(db.EmailEvents.Where(e => e.Kind == EmailEventKind.Engagement && e.OccurredAtUtc >= previousFrom).Select(e => e.OccurredAtUtc), ct);
        var bounced = await DailyAsync(db.EmailEvents.Where(e => e.Kind == EmailEventKind.Delivery && e.Status == "Bounced" && e.OccurredAtUtc >= previousFrom).Select(e => e.OccurredAtUtc), ct);

        var uniqueClickers = await db.EmailEvents
            .Where(e => e.Kind == EmailEventKind.Engagement && e.OccurredAtUtc >= from && e.CampaignRecipientId != null)
            .Select(e => e.CampaignRecipientId).Distinct().CountAsync(ct);
        var hourAgo = now.AddHours(-1);
        var sentLastHour = await db.CampaignRecipients.CountAsync(r => r.SentAtUtc > hourAgo, ct);
        var lastReport = await db.EmailEvents.MaxAsync(e => (DateTime?)e.OccurredAtUtc, ct);
        var subscribed = await db.Contacts.CountAsync(c => c.Status == ContactStatus.Subscribed, ct);

        return new Summary(from, now, Build(sent, from), Build(delivered, from), Build(clicks, from), Build(bounced, from),
            uniqueClickers, sentLastHour, lastReport, subscribed);
    }

    private static async Task<Dictionary<DateTime, int>> DailyAsync(IQueryable<DateTime> times, CancellationToken ct) =>
        await times.GroupBy(t => t.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Day, x => x.Count, ct);

    private static Series Build(Dictionary<DateTime, int> byDay, DateTime from)
    {
        var daily = Enumerable.Range(0, Days).Select(i => byDay.GetValueOrDefault(from.AddDays(i))).ToArray();
        var previous = byDay.Where(kv => kv.Key < from).Sum(kv => kv.Value);
        return new Series(daily, daily.Sum(), previous);
    }

    /// <summary>"+18%", "−4%", or "new" when there is nothing to compare with.</summary>
    public static string Change(int current, int previous) =>
        previous == 0 ? (current == 0 ? "—" : "new") : $"{(current >= previous ? "+" : "−")}{Math.Abs(100.0 * (current - previous) / previous):0}%";
}
