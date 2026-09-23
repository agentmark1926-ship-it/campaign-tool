using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>Owner-editable settings. Values saved on the Settings page override the App Service configuration.</summary>
public class EffectiveSettings
{
    public string SenderAddress { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    public string MailingAddress { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public int MaxPerMinute { get; set; }
    public int MaxPerHour { get; set; }
}

public class SettingsService(AppDbContext db, IOptions<AppOptions> app, IOptions<SendingOptions> sending, IOptions<AcsOptions> acs, ILogger<SettingsService> log)
{
    private const string ReplyToKey = "ReplyTo", MailingAddressKey = "App:MailingAddress", TimeZoneKey = "App:TimeZone",
        MaxPerMinuteKey = "Sending:MaxPerMinute", MaxPerHourKey = "Sending:MaxPerHour";

    public async Task<EffectiveSettings> GetAsync(CancellationToken ct = default)
    {
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string Get(string key, string fallback) => stored.TryGetValue(key, out var v) ? v : fallback;
        int GetInt(string key, int fallback) => stored.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

        return new EffectiveSettings
        {
            SenderAddress = acs.Value.SenderAddress,
            ReplyTo = Get(ReplyToKey, ""),
            MailingAddress = Get(MailingAddressKey, app.Value.MailingAddress),
            TimeZone = Get(TimeZoneKey, app.Value.TimeZone),
            MaxPerMinute = GetInt(MaxPerMinuteKey, sending.Value.MaxPerMinute),
            MaxPerHour = GetInt(MaxPerHourKey, sending.Value.MaxPerHour),
        };
    }

    /// <summary>Returns the validation errors; saves only when there are none.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(EffectiveSettings s, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (!EmailRules.IsValid(EmailRules.Normalize(s.ReplyTo)))
            errors.Add("Reply-to must be a monitored mailbox, e.g. you@yourdomain.com.");
        if (string.IsNullOrWhiteSpace(s.MailingAddress))
            errors.Add("Mailing address is required; it is printed in every email footer.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(s.TimeZone ?? "", out _))
            errors.Add($"'{s.TimeZone}' is not a known time zone; use an IANA name such as America/Chicago.");
        if (s.MaxPerMinute < 1 || s.MaxPerHour < 1 || s.MaxPerMinute > s.MaxPerHour)
            errors.Add("Sending limits must be at least 1, and the per-minute limit cannot exceed the per-hour limit.");
        if (errors.Count > 0) return errors;

        var values = new Dictionary<string, string>
        {
            [ReplyToKey] = EmailRules.Normalize(s.ReplyTo),
            [MailingAddressKey] = s.MailingAddress.Trim(),
            [TimeZoneKey] = (s.TimeZone ?? "").Trim(),
            [MaxPerMinuteKey] = s.MaxPerMinute.ToString(),
            [MaxPerHourKey] = s.MaxPerHour.ToString(),
        };
        var existing = await db.Settings.Where(x => values.Keys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, ct);
        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var row)) row.Value = value;
            else db.Settings.Add(new Setting { Key = key, Value = value });
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("Settings saved: limits {MaxPerMinute}/min {MaxPerHour}/h, time zone {TimeZone}", s.MaxPerMinute, s.MaxPerHour, s.TimeZone);
        return errors;
    }
}
