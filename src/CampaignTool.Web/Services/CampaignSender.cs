using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// One pass of the sending pipeline, called by the CampaignWorker in a loop:
/// recover stale claims → start due scheduled campaigns → claim a batch within the rate limit → send → record → notify.
/// The database is the queue; one campaign sends at a time, oldest StartedAtUtc first.
/// </summary>
public class CampaignSender(AppDbContext db, IEmailSender sender, SettingsService settings, IOptionsMonitor<SendingOptions> sending, UnsubscribeTokenService unsubscribe,
    RateLimiter limiter, TimeProvider clock, CampaignProgressNotifier progress, ILogger<CampaignSender> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Claimed rows older than 10 minutes (the process died mid-send) become Unknown and are never resent automatically.</summary>
    public async Task<int> RecoverStaleClaimsAsync(CancellationToken ct = default)
    {
        var cutoff = Now - CampaignRules.ClaimTimeout;
        var n = await db.CampaignRecipients.Where(r => r.Status == RecipientStatus.Claimed && r.ClaimedAtUtc < cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatus.Unknown)
                .SetProperty(r => r.LastError, "The app stopped while sending; it may or may not have been sent."), ct);
        if (n > 0) log.LogWarning("{Count} recipient(s) Claimed -> Unknown after a restart", n);
        return n;
    }

    public async Task StartDueScheduledAsync(CancellationToken ct = default)
    {
        var due = await db.Campaigns.Where(c => c.Status == CampaignStatus.Scheduled && c.ScheduledAtUtc <= Now).ToListAsync(ct);
        foreach (var c in due)
        {
            CampaignRules.Move(c, CampaignStatus.Sending);
            c.StartedAtUtc = Now;
            log.LogInformation("Campaign {CampaignId} Scheduled -> Sending", c.Id);
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sends at most one batch. Returns the time the worker may try again: now if there is more to do right away,
    /// the next free rate-limit slot, or null when nothing is sending.
    /// </summary>
    public async Task<DateTime?> SendBatchAsync(CancellationToken ct = default)
    {
        var campaign = await db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Sending)
            .OrderBy(c => c.StartedAtUtc).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (campaign is null) return null;

        var open = await db.CampaignRecipients.Where(r => r.CampaignId == campaign.Id && (r.Status == RecipientStatus.Pending || r.Status == RecipientStatus.Claimed))
            .Select(r => r.NextAttemptAtUtc).ToListAsync(ct);
        if (open.Count == 0)
        {
            await CompleteAsync(campaign.Id, ct);
            return Now;
        }

        var s = await settings.GetAsync(ct);
        var available = limiter.Available(Now, s.MaxPerMinute, s.MaxPerHour);
        if (available == 0) return limiter.NextAvailable(Now, s.MaxPerMinute, s.MaxPerHour);

        var batchSize = Math.Min(Math.Max(1, sending.CurrentValue.BatchSize), available);
        var claimed = await ClaimAsync(campaign.Id, batchSize, ct);
        if (claimed.Count == 0)
        {
            // Only rows waiting for a retry remain: come back when the earliest is due.
            var nextRetry = open.Where(t => t is not null).Min();
            return nextRetry ?? Now.AddSeconds(15);
        }

        var contacts = await db.Contacts.AsNoTracking().Where(c => claimed.Select(r => r.ContactId).Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var emails = contacts.Values.Select(c => c.EmailNormalized).ToList();
        var suppressed = (await db.Suppressions.Where(x => emails.Contains(x.EmailNormalized)).Select(x => x.EmailNormalized).ToListAsync(ct)).ToHashSet();

        foreach (var r in claimed)
        {
            // Pause or cancel between messages: a pause puts the rest back in the queue, a cancel cancels them.
            var status = await db.Campaigns.Where(c => c.Id == campaign.Id).Select(c => c.Status).SingleAsync(ct);
            if (status != CampaignStatus.Sending)
            {
                var back = status == CampaignStatus.Cancelled ? RecipientStatus.Cancelled : RecipientStatus.Pending;
                await db.CampaignRecipients.Where(x => x.Id == r.Id && x.Status == RecipientStatus.Claimed)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, back), ct);
                continue;
            }

            // Re-check consent at send time: someone may have unsubscribed after the campaign was scheduled.
            if (!contacts.TryGetValue(r.ContactId, out var contact) || contact.Status != ContactStatus.Subscribed || suppressed.Contains(contact.EmailNormalized))
            {
                await SetAsync(r.Id, RecipientStatus.Cancelled, "No longer subscribed at send time", ct);
                continue;
            }

            limiter.Record(Now);
            var email = EmailComposer.Compose(campaign, contact, s.MailingAddress, unsubscribe.Url(contact.Id, campaign.Id)) with { To = r.EmailSnapshot };
            var result = await sender.SendAsync(email, ct);
            await RecordAsync(campaign.Id, r, contact, result, ct);
        }

        await db.Campaigns.Where(c => c.Id == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastBatchAtUtc, Now), ct);
        progress.Notify(campaign.Id);
        return Now;
    }

    /// <summary>UPDATE TOP (n) … OUTPUT: atomically claims due Pending rows so no two passes can send the same row.</summary>
    private async Task<List<CampaignRecipient>> ClaimAsync(int campaignId, int n, CancellationToken ct) =>
        await db.CampaignRecipients.FromSqlInterpolated($"""
            UPDATE TOP ({n}) CampaignRecipients
            SET Status = 'Claimed', ClaimedAtUtc = {Now}
            OUTPUT inserted.*
            WHERE CampaignId = {campaignId} AND Status = 'Pending' AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= {Now})
            """).AsNoTracking().ToListAsync(ct);

    private async Task RecordAsync(int campaignId, CampaignRecipient r, Contact contact, SendResult result, CancellationToken ct)
    {
        if (result.Success)
        {
            await db.CampaignRecipients.Where(x => x.Id == r.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, RecipientStatus.Sent).SetProperty(x => x.AcsMessageId, result.MessageId)
                .SetProperty(x => x.SentAtUtc, Now).SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1).SetProperty(x => x.LastError, (string?)null), ct);
            await db.Campaigns.Where(c => c.Id == campaignId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Sent, c => c.Sent + 1), ct);
            return;
        }

        var failures = r.AttemptCount + 1;
        var retryAt = result.Transient ? CampaignRules.NextAttempt(failures, Now) : null;
        if (retryAt is not null)
        {
            await db.CampaignRecipients.Where(x => x.Id == r.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, RecipientStatus.Pending).SetProperty(x => x.AttemptCount, failures)
                .SetProperty(x => x.NextAttemptAtUtc, retryAt).SetProperty(x => x.LastError, Truncate(result.Error)), ct);
            log.LogInformation("Recipient {RecipientId} transient failure {Attempt}, retry at {RetryAt:u}", r.Id, failures, retryAt);
            return;
        }

        await db.CampaignRecipients.Where(x => x.Id == r.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Status, RecipientStatus.Failed).SetProperty(x => x.AttemptCount, failures)
            .SetProperty(x => x.LastError, Truncate(result.Error)), ct);
        await db.Campaigns.Where(c => c.Id == campaignId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Failed, c => c.Failed + 1), ct);
        if (!result.Transient)
        {
            await db.Contacts.Where(c => c.Id == contact.Id).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ContactStatus.Invalid).SetProperty(c => c.StatusReason, "Address rejected by the email service")
                .SetProperty(c => c.StatusChangedAtUtc, Now), ct);
            log.LogInformation("Contact {ContactId} {From} -> Invalid (address rejected)", contact.Id, contact.Status);
        }
        else
        {
            log.LogWarning("Recipient {RecipientId} failed after {Attempts} attempts", r.Id, failures);
        }
    }

    private async Task CompleteAsync(int campaignId, CancellationToken ct)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == campaignId, ct);
        if (c.Status != CampaignStatus.Sending) return;
        CampaignRules.Move(c, CampaignStatus.Completed);
        c.CompletedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Campaign {CampaignId} Sending -> Completed: {Sent} sent, {Failed} failed", c.Id, c.Sent, c.Failed);
        progress.Notify(c.Id);
    }

    private Task SetAsync(long id, RecipientStatus status, string reason, CancellationToken ct) =>
        db.CampaignRecipients.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, status).SetProperty(x => x.LastError, reason), ct);

    private static string? Truncate(string? s) => s is { Length: > 1000 } ? s[..1000] : s;
}
