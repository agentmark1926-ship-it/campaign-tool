using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

public class UnsubscribeService(AppDbContext db, TimeProvider clock, CampaignProgressNotifier progress, ILogger<UnsubscribeService> log)
{
    /// <summary>
    /// Idempotent: the contact becomes Unsubscribed, the address is suppressed, and the campaign's Unsubscribed count goes up
    /// only the first time. Works even if the campaign has since been deleted; a deleted contact is simply a no-op.
    /// </summary>
    public async Task UnsubscribeAsync(int contactId, int campaignId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var contact = await db.Contacts.SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return;

        if (!await db.Suppressions.AnyAsync(s => s.EmailNormalized == contact.EmailNormalized, ct))
            db.Suppressions.Add(new Suppression { EmailNormalized = contact.EmailNormalized, Reason = SuppressionReason.Unsubscribe, Source = $"campaign {campaignId}", CreatedAtUtc = now });

        var changed = contact.Status != ContactStatus.Unsubscribed;
        if (changed)
        {
            log.LogInformation("Contact {ContactId} {From} -> Unsubscribed (link in campaign {CampaignId})", contact.Id, contact.Status, campaignId);
            contact.Status = ContactStatus.Unsubscribed;
            contact.StatusReason = "Clicked unsubscribe";
            contact.StatusChangedAtUtc = contact.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);

        if (changed && await db.Campaigns.Where(c => c.Id == campaignId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Unsubscribed, c => c.Unsubscribed + 1), ct) == 1)
            progress.Notify(campaignId);
    }
}
