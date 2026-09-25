using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// Per sending domain: the owner's daily cap (for warm-up), today's and the last hour's sends, and a 30-day health read
/// from our own delivery reports (bounces, spam filtering, failures, unsubscribes).
/// </summary>
public class DomainService(AppDbContext db, IOptions<AcsOptions> acs, SettingsService settings, TimeProvider clock)
{
    public const int WindowDays = 30;
    private const string LimitKeyPrefix = "DomainDailyLimit:";

    public enum Health { New, Healthy, Watch, AtRisk }

    public record Stats(int Sent, int Delivered, int Bounced, int Failed, int SpamFiltered, int Unsubscribed, int Clickers)
    {
        public double Rate(int part) => Sent == 0 ? 0 : (double)part / Sent;
    }

    public record DomainStatus(
        string Domain, IReadOnlyList<string> Addresses, bool IsTestDomain,
        int DailyLimit, int SentToday, int SentLastHour, Stats Last30Days,
        Health Health, int Score, IReadOnlyList<string> Notes);

    public static string DomainOf(string? address) =>
        (address ?? "").Split('@') is [_, var d] ? d.Trim().ToLowerInvariant() : "";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<DomainStatus>> ListAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var domains = acs.Value.AllowedSenders().GroupBy(DomainOf).Where(g => g.Key.Length > 0).ToList();
        var limits = await LimitsAsync(ct);
        var today = await SentSinceByDomainAsync(StartOfTodayUtc(s.TimeZone), ct);
        var hour = await SentSinceByDomainAsync(Now.AddHours(-1), ct);
        var stats = await StatsByDomainAsync(Now.AddDays(-WindowDays), ct);

        return domains.Select(g =>
        {
            var st = stats.GetValueOrDefault(g.Key) ?? new Stats(0, 0, 0, 0, 0, 0, 0);
            var (health, score, notes) = Assess(st);
            return new DomainStatus(g.Key, g.ToList(), g.Key.EndsWith(".azurecomm.net"), limits.GetValueOrDefault(g.Key),
                today.GetValueOrDefault(g.Key), hour.GetValueOrDefault(g.Key), st, health, score, notes);
        }).OrderBy(d => d.IsTestDomain).ThenBy(d => d.Domain).ToList();
    }

    /// <summary>0 removes the cap. Returns an error sentence, or null.</summary>
    public async Task<string?> SetDailyLimitAsync(string domain, int limit, CancellationToken ct = default)
    {
        if (limit < 0) return "The daily limit can't be negative; use 0 for no limit.";
        var key = LimitKeyPrefix + domain.Trim().ToLowerInvariant();
        var row = await db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = limit.ToString() });
        else row.Value = limit.ToString();
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>How many more emails each capped domain may send today (domains without a cap are absent).</summary>
    public async Task<Dictionary<string, int>> RemainingTodayAsync(string timeZone, CancellationToken ct = default)
    {
        var limits = (await LimitsAsync(ct)).Where(kv => kv.Value > 0).ToDictionary();
        if (limits.Count == 0) return [];
        var today = await SentSinceByDomainAsync(StartOfTodayUtc(timeZone), ct);
        return limits.ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value - today.GetValueOrDefault(kv.Key)));
    }

    /// <summary>Midnight tonight in the owner's time zone, as UTC: when daily caps reset.</summary>
    public DateTime StartOfTomorrowUtc(string timeZone) => StartOfTodayUtc(timeZone).AddDays(1);

    public DateTime StartOfTodayUtc(string timeZone)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var tz)) return Now.Date;
        var localMidnight = TimeZoneInfo.ConvertTimeFromUtc(Now, tz).Date;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// Thresholds follow what mailbox providers act on: hard bounces above 2% or spam filtering above 0.3% put a domain at risk;
    /// half of that is worth watching. Under 50 sends there isn't enough to judge (a new or warming domain).
    /// </summary>
    public static (Health Health, int Score, IReadOnlyList<string> Notes) Assess(Stats s)
    {
        var bounce = s.Rate(s.Bounced) * 100;
        var spam = s.Rate(s.SpamFiltered) * 100;
        var failed = s.Rate(s.Failed) * 100;
        var unsub = s.Rate(s.Unsubscribed) * 100;
        var score = (int)Math.Round(Math.Clamp(100 - 15 * bounce - 150 * spam - 3 * failed - 10 * unsub, 0, 100));
        var notes = new List<string>();
        if (s.Sent < 50)
            return (Health.New, score, ["Fewer than 50 emails in the last 30 days: keep warming up with small, engaged sends."]);

        var health = Health.Healthy;
        void Flag(bool atRisk, bool watch, string note)
        {
            if (atRisk) { health = Health.AtRisk; notes.Add(note); }
            else if (watch) { if (health == Health.Healthy) health = Health.Watch; notes.Add(note); }
        }
        Flag(bounce >= 2, bounce >= 1, $"Bounces {bounce:0.00}% (keep under 2%): clean the list or remove old addresses.");
        Flag(spam >= 0.3, spam >= 0.1, $"Spam filtering {spam:0.00}% (keep under 0.3%): send only to people who opted in, and make unsubscribing easy.");
        Flag(false, failed >= 5, $"Failed sends {failed:0.0}%: check the delivery reports for the reason.");
        Flag(false, unsub >= 1, $"Unsubscribes {unsub:0.00}%: content or frequency may not match what subscribers expect.");
        if (notes.Count == 0) notes.Add("Bounces, spam filtering and unsubscribes are within healthy ranges.");
        return (health, score, notes);
    }

    private async Task<Dictionary<string, int>> LimitsAsync(CancellationToken ct) =>
        (await db.Settings.AsNoTracking().Where(x => x.Key.StartsWith(LimitKeyPrefix)).ToListAsync(ct))
            .ToDictionary(x => x.Key[LimitKeyPrefix.Length..], x => int.TryParse(x.Value, out var v) ? v : 0);

    private async Task<Dictionary<string, int>> SentSinceByDomainAsync(DateTime sinceUtc, CancellationToken ct) =>
        (await db.CampaignRecipients.Where(r => r.SentAtUtc >= sinceUtc)
            .GroupBy(r => r.Campaign.FromEmail).Select(g => new { From = g.Key, Count = g.Count() }).ToListAsync(ct))
        .GroupBy(x => DomainOf(x.From)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

    private async Task<Dictionary<string, Stats>> StatsByDomainAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var sends = await db.CampaignRecipients.Where(r => r.SentAtUtc >= sinceUtc)
            .GroupBy(r => r.Campaign.FromEmail)
            .Select(g => new
            {
                From = g.Key,
                Sent = g.Count(),
                Delivered = g.Count(r => r.DeliveredAtUtc != null),
                Bounced = g.Count(r => r.Status == RecipientStatus.Bounced),
                Failed = g.Count(r => r.Status == RecipientStatus.Failed),
                Clickers = g.Count(r => r.ClickCount > 0),
            }).ToListAsync(ct);
        var spam = await db.EmailEvents
            .Where(e => e.OccurredAtUtc >= sinceUtc && e.CampaignRecipientId != null && (e.Status == "FilteredSpam" || e.Status == "Quarantined"))
            .GroupBy(e => e.CampaignRecipient!.Campaign.FromEmail).Select(g => new { From = g.Key, Count = g.Count() }).ToListAsync(ct);
        var unsubs = await db.Campaigns.Where(c => c.StartedAtUtc >= sinceUtc)
            .GroupBy(c => c.FromEmail).Select(g => new { From = g.Key, Count = g.Sum(c => c.Unsubscribed) }).ToListAsync(ct);

        return sends.Select(x => x.From).Concat(spam.Select(x => x.From)).Concat(unsubs.Select(x => x.From))
            .GroupBy(DomainOf)
            .ToDictionary(g => g.Key, g =>
            {
                var from = g.ToHashSet();
                var sd = sends.Where(x => from.Contains(x.From)).ToList();
                return new Stats(sd.Sum(x => x.Sent), sd.Sum(x => x.Delivered), sd.Sum(x => x.Bounced), sd.Sum(x => x.Failed),
                    spam.Where(x => from.Contains(x.From)).Sum(x => x.Count), unsubs.Where(x => from.Contains(x.From)).Sum(x => x.Count),
                    sd.Sum(x => x.Clickers));
            });
    }
}
