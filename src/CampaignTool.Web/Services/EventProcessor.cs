using System.Text.Json;
using CampaignTool.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>
/// Stores each ACS email event once (EventId is unique) and applies it once, in the same transaction:
/// recipient status, campaign counters, bounces and suppression. Events for unknown messages (e.g. test sends) are stored and logged.
/// </summary>
public class EventProcessor(AppDbContext db, TimeProvider clock, CampaignProgressNotifier progress, ILogger<EventProcessor> log)
{
    public const string DeliveryReport = "Microsoft.Communication.EmailDeliveryReportReceived";
    public const string EngagementReport = "Microsoft.Communication.EmailEngagementTrackingReportReceived";

    /// <summary>Three soft bounces in a row count as a hard bounce.</summary>
    public const int SoftBounceLimit = 3;

    /// <summary>FilteredSpam above this share of Sent pauses the campaign.</summary>
    public const double SpamPauseThreshold = 0.005;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Returns false when the event was a duplicate or not an email event.</summary>
    public async Task<bool> StoreAsync(JsonElement ev, CancellationToken ct = default)
    {
        var eventType = ev.GetProperty("eventType").GetString();
        var kind = eventType switch
        {
            DeliveryReport => EmailEventKind.Delivery,
            EngagementReport => EmailEventKind.Engagement,
            _ => (EmailEventKind?)null,
        };
        if (kind is null)
        {
            log.LogWarning("Ignored event type {EventType}", eventType);
            return false;
        }

        var eventId = ev.GetProperty("id").GetString() ?? "";
        if (await db.EmailEvents.AnyAsync(e => e.EventId == eventId, ct))
            return false;

        var data = ev.GetProperty("data");
        var messageId = Str(data, "messageId") ?? "";
        var recipient = await db.CampaignRecipients.AsNoTracking().FirstOrDefaultAsync(r => r.AcsMessageId == messageId, ct);
        var row = new EmailEvent
        {
            EventId = eventId,
            AcsMessageId = messageId,
            Kind = kind.Value,
            Status = (kind == EmailEventKind.Delivery ? Str(data, "status") : Str(data, "engagementType")) ?? "",
            Url = kind == EmailEventKind.Engagement ? Truncate(Str(data, "engagementContext"), 2048) : null,
            UserAgent = Truncate(Str(data, "userAgent"), 512),
            OccurredAtUtc = ParseTime(Str(data, "deliveryAttemptTimeStamp") ?? Str(data, "userActionTimeStamp") ?? Str(ev, "eventTime")),
            RawJson = ev.GetRawText(),
            CampaignRecipientId = recipient?.Id,
        };

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                db.EmailEvents.Add(row);
                await db.SaveChangesAsync(ct);
                if (recipient is not null)
                {
                    if (row.Kind == EmailEventKind.Delivery) await ApplyDeliveryAsync(recipient, row.Status, Str(data.TryGetProperty("deliveryStatusDetails", out var d) ? d : default, "statusMessage"), ct);
                    else if (row.Status.Equals("click", StringComparison.OrdinalIgnoreCase)) await ApplyClickAsync(recipient, ct);
                }
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return false; // concurrent duplicate delivery
        }

        if (recipient is null)
        {
            log.LogInformation("Stored {Kind} event {Status} for unknown message {MessageId}", row.Kind, row.Status, messageId);
        }
        else
        {
            log.LogInformation("Applied {Kind} event {Status} to recipient {RecipientId}", row.Kind, row.Status, recipient.Id);
            progress.Notify(recipient.CampaignId);
        }
        return true;
    }

    private async Task ApplyDeliveryAsync(CampaignRecipient r, string status, string? detail, CancellationToken ct)
    {
        var campaignId = r.CampaignId;
        // Only a row the worker handed to ACS moves; a late or repeated event can't undo a final outcome.
        var sent = db.CampaignRecipients.Where(x => x.Id == r.Id && x.Status == RecipientStatus.Sent);
        async Task<bool> Move(RecipientStatus to, string? error = null) => to == RecipientStatus.Delivered
            ? await sent.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.DeliveredAtUtc, Now), ct) == 1
            : await sent.ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, to).SetProperty(x => x.LastError, error), ct) == 1;

        switch (status)
        {
            case "Delivered":
                if (await Move(RecipientStatus.Delivered))
                {
                    await Counter(campaignId, nameof(Campaign.Delivered), ct);
                    await db.Contacts.Where(c => c.Id == r.ContactId).ExecuteUpdateAsync(u => u.SetProperty(c => c.SoftBounceCount, 0), ct);
                }
                break;

            case "Bounced":
                if (await Move(RecipientStatus.Bounced, detail ?? "Bounced"))
                {
                    await Counter(campaignId, nameof(Campaign.Bounced), ct);
                    await HardBounceAsync(r.ContactId, detail ?? "Hard bounce", ct);
                }
                break;

            case "Suppressed":
                // ACS's own suppression list blocked the address: treat it as bounced.
                if (await Move(RecipientStatus.Failed, "Suppressed by ACS"))
                {
                    await Counter(campaignId, nameof(Campaign.Failed), ct);
                    await HardBounceAsync(r.ContactId, "Suppressed by the email service", ct);
                }
                break;

            case "Failed":
                // Temporary failure after ACS's own retries (mailbox full, server busy): a soft bounce.
                if (await Move(RecipientStatus.Failed, detail ?? "Failed"))
                {
                    await Counter(campaignId, nameof(Campaign.Failed), ct);
                    await db.Contacts.Where(c => c.Id == r.ContactId).ExecuteUpdateAsync(u => u.SetProperty(c => c.SoftBounceCount, c => c.SoftBounceCount + 1), ct);
                    var count = await db.Contacts.Where(c => c.Id == r.ContactId).Select(c => c.SoftBounceCount).SingleAsync(ct);
                    if (count >= SoftBounceLimit) await HardBounceAsync(r.ContactId, $"{count} soft bounces in a row", ct);
                }
                break;

            case "FilteredSpam":
            case "Quarantined":
                if (await Move(RecipientStatus.Failed, status))
                {
                    await Counter(campaignId, nameof(Campaign.Failed), ct);
                    if (status == "FilteredSpam") await SpamGuardAsync(campaignId, ct);
                }
                break;

            default:
                log.LogInformation("Delivery status {Status} stored without a recipient change", status);
                break;
        }
    }

    private async Task ApplyClickAsync(CampaignRecipient r, CancellationToken ct)
    {
        await db.CampaignRecipients.Where(x => x.Id == r.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), ct);
        var clicks = await db.CampaignRecipients.Where(x => x.Id == r.Id).Select(x => x.ClickCount).SingleAsync(ct);
        if (clicks == 1) await Counter(r.CampaignId, nameof(Campaign.Clicked), ct); // once per recipient
    }

    /// <summary>The contact becomes Bounced and the address itself is suppressed.</summary>
    private async Task HardBounceAsync(int contactId, string reason, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return;
        if (contact.Status != ContactStatus.Bounced)
        {
            await db.Contacts.Where(c => c.Id == contactId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ContactStatus.Bounced).SetProperty(c => c.StatusReason, reason).SetProperty(c => c.StatusChangedAtUtc, Now), ct);
            log.LogInformation("Contact {ContactId} {From} -> Bounced ({Reason})", contactId, contact.Status, reason);
        }
        if (!await db.Suppressions.AnyAsync(s => s.EmailNormalized == contact.EmailNormalized, ct))
        {
            db.Suppressions.Add(new Suppression { EmailNormalized = contact.EmailNormalized, Reason = SuppressionReason.HardBounce, Source = "delivery report", CreatedAtUtc = Now });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Spam complaints aren't reported separately; rising FilteredSpam is the signal. Above 0.5% of Sent the campaign is paused.</summary>
    private async Task SpamGuardAsync(int campaignId, CancellationToken ct)
    {
        var c = await db.Campaigns.SingleAsync(x => x.Id == campaignId, ct);
        var spam = await db.CampaignRecipients.CountAsync(x => x.CampaignId == campaignId && x.LastError == "FilteredSpam", ct);
        if (c.Status == CampaignStatus.Sending && c.Sent > 0 && (double)spam / c.Sent > SpamPauseThreshold)
        {
            CampaignRules.Move(c, CampaignStatus.Paused);
            await db.SaveChangesAsync(ct);
            log.LogWarning("Campaign {CampaignId} Sending -> Paused: {Spam} of {Sent} filtered as spam", campaignId, spam, c.Sent);
        }
    }

    private Task Counter(int campaignId, string counter, CancellationToken ct)
    {
        var campaign = db.Campaigns.Where(c => c.Id == campaignId);
        return counter switch
        {
            nameof(Campaign.Delivered) => campaign.ExecuteUpdateAsync(u => u.SetProperty(c => c.Delivered, c => c.Delivered + 1), ct),
            nameof(Campaign.Bounced) => campaign.ExecuteUpdateAsync(u => u.SetProperty(c => c.Bounced, c => c.Bounced + 1), ct),
            nameof(Campaign.Failed) => campaign.ExecuteUpdateAsync(u => u.SetProperty(c => c.Failed, c => c.Failed + 1), ct),
            nameof(Campaign.Clicked) => campaign.ExecuteUpdateAsync(u => u.SetProperty(c => c.Clicked, c => c.Clicked + 1), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(counter)),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;

    private static DateTime ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, out var t) ? t.UtcDateTime : DateTime.UtcNow;
}
