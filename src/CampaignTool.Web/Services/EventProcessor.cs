using System.Text.Json;
using CampaignTool.Web.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

/// <summary>Stores each ACS email event once (EventId is unique). Events for unknown messages, such as test sends, are stored and logged.</summary>
public class EventProcessor(AppDbContext db, ILogger<EventProcessor> log)
{
    public const string DeliveryReport = "Microsoft.Communication.EmailDeliveryReportReceived";
    public const string EngagementReport = "Microsoft.Communication.EmailEngagementTrackingReportReceived";

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
            CampaignRecipientId = await db.CampaignRecipients.Where(r => r.AcsMessageId == messageId).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct),
        };
        db.EmailEvents.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.Entry(row).State = EntityState.Detached;
            return false; // concurrent duplicate delivery
        }

        if (row.CampaignRecipientId is null)
            log.LogInformation("Stored {Kind} event {Status} for unknown message {MessageId}", row.Kind, row.Status, messageId);
        else
            log.LogInformation("Stored {Kind} event {Status} for recipient {RecipientId}", row.Kind, row.Status, row.CampaignRecipientId);
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Truncate(string? s, int max) => s is { Length: > 0 } && s.Length > max ? s[..max] : s;

    private static DateTime ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, out var t) ? t.UtcDateTime : DateTime.UtcNow;
}
