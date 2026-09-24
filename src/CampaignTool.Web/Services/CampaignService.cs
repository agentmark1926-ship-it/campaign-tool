using System.Text.Json;
using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>Raised after every sending batch and every applied event so open monitor pages re-render without polling.</summary>
public class CampaignProgressNotifier
{
    public event Action<int>? Changed;
    public void Notify(int campaignId) => Changed?.Invoke(campaignId);
}

/// <summary>Owner actions on campaigns. Every status change logs one line.</summary>
public class CampaignService(AppDbContext db, SettingsService settings, UnsubscribeTokenService unsubscribe, TimeProvider clock, CampaignProgressNotifier progress, ILogger<CampaignService> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Campaign> CreateAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        var campaign = new Campaign
        {
            Name = $"Campaign {TimeFormat.Local(Now, s.TimeZone, "MMM d, yyyy")}",
            FromEmail = s.SenderAddress,
            FromName = "",
            ReplyTo = s.ReplyTo,
            CreatedAtUtc = Now,
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} created", campaign.Id);
        return campaign;
    }

    public async Task<Campaign> DuplicateAsync(int id, CancellationToken ct = default)
    {
        var src = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == id, ct);
        var copy = new Campaign
        {
            Name = $"{src.Name} (copy)", Subject = src.Subject, Preheader = src.Preheader, FromName = src.FromName, FromEmail = src.FromEmail,
            ReplyTo = src.ReplyTo, ListId = src.ListId, ExcludeListIds = src.ExcludeListIds, TemplateId = src.TemplateId,
            Format = src.Format, Html = src.Html, Text = src.Text, CreatedAtUtc = Now,
        };
        db.Campaigns.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task<string?> DeleteDraftAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == id, ct);
        if (c.Status != CampaignStatus.Draft) return "Only drafts can be deleted; sent campaigns are kept for their results.";
        db.Campaigns.Remove(c);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Saves the editable fields of a draft. Returns an error sentence, or null.</summary>
    public async Task<string?> SaveDraftAsync(Campaign edited, CancellationToken ct = default)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == edited.Id, ct);
        if (c.Status != CampaignStatus.Draft) return "This campaign is no longer a draft, so it can't be edited.";
        var s = await settings.GetAsync(ct);
        if (TemplateRenderer.Validate(edited.Subject) is { } se) return $"The subject's merge field doesn't parse: {se}";
        if (TemplateRenderer.Validate(edited.Body) is { } be) return $"A merge field in the email doesn't parse: {be}";
        c.Name = edited.Name.Trim();
        c.Subject = edited.Subject.Trim();
        c.Preheader = string.IsNullOrWhiteSpace(edited.Preheader) ? null : edited.Preheader.Trim();
        if (!s.AllowedSenders.Contains(edited.FromEmail, StringComparer.OrdinalIgnoreCase))
            return $"'{edited.FromEmail}' isn't set up in Azure; pick a From address from the list.";
        c.FromEmail = edited.FromEmail;
        c.ReplyTo = string.IsNullOrWhiteSpace(edited.ReplyTo) ? null : EmailRules.Normalize(edited.ReplyTo);
        c.ListId = edited.ListId;
        c.ExcludeListIds = edited.ExcludeListIds;
        c.TemplateId = edited.TemplateId;
        c.Format = edited.Format;
        c.Html = edited.Html;
        c.Text = edited.Text;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public static List<int> ExcludedLists(Campaign c) => JsonSerializer.Deserialize<List<int>>(c.ExcludeListIds) ?? [];

    /// <summary>Contacts that would receive the campaign: in the list, not in an excluded list, Subscribed, not suppressed.</summary>
    public IQueryable<Contact> Sendable(int listId, IReadOnlyCollection<int> excluded) =>
        db.Contacts.Where(c => c.Status == ContactStatus.Subscribed
            && c.ListContacts.Any(lc => lc.ListId == listId)
            && !c.ListContacts.Any(lc => excluded.Contains(lc.ListId))
            && !db.Suppressions.Any(s => s.EmailNormalized == c.EmailNormalized));

    /// <summary>Everything that must be true before a send or schedule. Returns the first problem, or null.</summary>
    public async Task<string?> ReadinessProblemAsync(Campaign c, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(s.MailingAddress)) return "Set the mailing address in Settings; every email must include it.";
        if (!unsubscribe.IsConfigured) return "Unsubscribe links aren't configured (Auth:UnsubscribeKey and App:BaseUrl); they are set by the Azure deployment.";
        if (string.IsNullOrWhiteSpace(c.Subject)) return "Add a subject line.";
        if (string.IsNullOrWhiteSpace(c.Body)) return "Add the email content.";
        if (string.IsNullOrWhiteSpace(c.ReplyTo)) return "Add a reply-to address that someone reads.";
        if (!s.AllowedSenders.Contains(c.FromEmail, StringComparer.OrdinalIgnoreCase))
            return $"The From address {c.FromEmail} is no longer set up in Azure; pick another on the Setup tab.";
        if (!await db.Lists.AnyAsync(l => l.Id == c.ListId, ct)) return "Choose the list to send to.";
        if (!await Sendable(c.ListId, ExcludedLists(c)).AnyAsync(ct)) return "Nobody on that list can be sent to (after unsubscribes, bounces, suppressions and exclusions).";
        return null;
    }

    /// <summary>
    /// Inserts one Pending recipient per sendable contact. The unique (CampaignId, ContactId) index makes a second insert for the same
    /// contact fail rather than duplicate; the NOT EXISTS keeps re-materialization idempotent. Returns rows inserted.
    /// </summary>
    public async Task<int> MaterializeAsync(Campaign c, CancellationToken ct = default)
    {
        var excluded = JsonSerializer.Serialize(ExcludedLists(c));
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO CampaignRecipients (CampaignId, ContactId, EmailSnapshot, Status, AttemptCount, ClickCount)
            SELECT {c.Id}, ct.Id, ct.Email, 'Pending', 0, 0
            FROM Contacts ct
            WHERE ct.Status = 'Subscribed'
              AND EXISTS (SELECT 1 FROM ListContacts lc WHERE lc.ContactId = ct.Id AND lc.ListId = {c.ListId})
              AND NOT EXISTS (SELECT 1 FROM ListContacts lx WHERE lx.ContactId = ct.Id AND lx.ListId IN (SELECT CAST([value] AS int) FROM OPENJSON({excluded})))
              AND NOT EXISTS (SELECT 1 FROM Suppressions s WHERE s.EmailNormalized = ct.EmailNormalized)
              AND NOT EXISTS (SELECT 1 FROM CampaignRecipients r WHERE r.CampaignId = {c.Id} AND r.ContactId = ct.Id)
            """, ct);
        c.Recipients = await db.CampaignRecipients.CountAsync(r => r.CampaignId == c.Id, ct);
        return inserted;
    }

    public async Task<string?> SendNowAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == id, ct);
        if (await ReadinessProblemAsync(c, ct) is { } problem) return problem;
        var n = await MaterializeAsync(c, ct);
        CampaignRules.Move(c, CampaignStatus.Sending);
        c.StartedAtUtc = Now;
        c.ScheduledAtUtc = null;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} Draft -> Sending ({Recipients} recipients)", c.Id, n);
        progress.Notify(c.Id);
        return null;
    }

    public async Task<string?> ScheduleAsync(int id, DateTime whenUtc, CancellationToken ct = default)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == id, ct);
        if (whenUtc <= Now.AddMinutes(1)) return "Pick a time at least a couple of minutes from now, or use Send now.";
        if (await ReadinessProblemAsync(c, ct) is { } problem) return problem;
        var n = await MaterializeAsync(c, ct);
        CampaignRules.Move(c, CampaignStatus.Scheduled);
        c.ScheduledAtUtc = whenUtc;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} Draft -> Scheduled for {When:u} ({Recipients} recipients)", c.Id, whenUtc, n);
        return null;
    }

    /// <summary>Back to Draft; the not-yet-sent recipient rows are removed so the next schedule picks up list changes.</summary>
    public async Task UnscheduleAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == id, ct);
        CampaignRules.Move(c, CampaignStatus.Draft);
        c.ScheduledAtUtc = null;
        await db.CampaignRecipients.Where(r => r.CampaignId == id && r.Status == RecipientStatus.Pending).ExecuteDeleteAsync(ct);
        c.Recipients = await db.CampaignRecipients.CountAsync(r => r.CampaignId == id, ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} Scheduled -> Draft", id);
    }

    public Task PauseAsync(int id, CancellationToken ct = default) => MoveAsync(id, CampaignStatus.Paused, ct);

    public Task ResumeAsync(int id, CancellationToken ct = default) => MoveAsync(id, CampaignStatus.Sending, ct);

    /// <summary>Stops the campaign; every recipient not yet sent becomes Cancelled.</summary>
    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        await MoveAsync(id, CampaignStatus.Cancelled, ct);
        var n = await db.CampaignRecipients.Where(r => r.CampaignId == id && r.Status == RecipientStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatus.Cancelled), ct);
        await db.Campaigns.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CompletedAtUtc, Now), ct);
        log.LogInformation("Campaign {CampaignId}: {Count} pending recipient(s) cancelled", id, n);
        progress.Notify(id);
    }

    /// <summary>The owner's explicit decision to resend an Unknown row (it may already have been delivered).</summary>
    public async Task RetryUnknownAsync(long recipientId, CancellationToken ct = default)
    {
        var n = await db.CampaignRecipients.Where(r => r.Id == recipientId && r.Status == RecipientStatus.Unknown)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatus.Pending).SetProperty(r => r.NextAttemptAtUtc, (DateTime?)null), ct);
        if (n > 0) log.LogInformation("Recipient {RecipientId} Unknown -> Pending (owner retry)", recipientId);
    }

    private async Task MoveAsync(int id, CampaignStatus to, CancellationToken ct)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == id, ct);
        var from = c.Status;
        CampaignRules.Move(c, to);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} {From} -> {To}", id, from, to);
        progress.Notify(id);
    }
}
