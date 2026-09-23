using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

public record ContactQuery(string? Search = null, ContactStatus? Status = null, int? ListId = null);

public record ListSummary(int Id, string Name, string? Description, int Members, int Sendable, DateTime CreatedAtUtc);

/// <summary>Contacts, lists and suppressions. Every contact status change logs one line.</summary>
public class ContactService(AppDbContext db, ILogger<ContactService> log)
{
    // ---------- Contacts ----------

    public IQueryable<Contact> Query(ContactQuery q)
    {
        var query = db.Contacts.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLowerInvariant();
            query = query.Where(c => c.EmailNormalized.Contains(s) || (c.FirstName != null && c.FirstName.Contains(s)) || (c.LastName != null && c.LastName.Contains(s)));
        }
        if (q.Status is { } status) query = query.Where(c => c.Status == status);
        if (q.ListId is { } listId) query = query.Where(c => c.ListContacts.Any(lc => lc.ListId == listId));
        return query;
    }

    /// <summary>Unsubscribes the contacts and suppresses their addresses.</summary>
    public async Task UnsubscribeAsync(IReadOnlyCollection<int> ids, string reason, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var contacts = await db.Contacts.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var emails = contacts.Select(c => c.EmailNormalized).ToList();
        var already = (await db.Suppressions.Where(s => emails.Contains(s.EmailNormalized)).Select(s => s.EmailNormalized).ToListAsync(ct)).ToHashSet();
        foreach (var c in contacts)
        {
            if (!already.Contains(c.EmailNormalized))
                db.Suppressions.Add(new Suppression { EmailNormalized = c.EmailNormalized, Reason = SuppressionReason.Unsubscribe, Source = reason, CreatedAtUtc = now });
            if (c.Status == ContactStatus.Unsubscribed) continue;
            log.LogInformation("Contact {ContactId} {From} -> Unsubscribed ({Reason})", c.Id, c.Status, reason);
            c.Status = ContactStatus.Unsubscribed;
            c.StatusReason = reason;
            c.StatusChangedAtUtc = now;
            c.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The deliberate re-subscribe. Refused while the address is suppressed. Returns an error sentence, or null.</summary>
    public async Task<string?> ResubscribeAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Contacts.SingleAsync(x => x.Id == id, ct);
        if (await db.Suppressions.AnyAsync(s => s.EmailNormalized == c.EmailNormalized, ct))
            return "This address is on the suppression list; remove it there first if you have the person's consent.";
        if (c.Status == ContactStatus.Subscribed) return null;
        log.LogInformation("Contact {ContactId} {From} -> Subscribed (re-subscribed by owner)", c.Id, c.Status);
        c.Status = ContactStatus.Subscribed;
        c.StatusReason = "Re-subscribed by owner";
        c.StatusChangedAtUtc = c.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var count = await db.Contacts.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
        log.LogInformation("Deleted {Count} contact(s): {ContactIds}", count, ids);
        return count;
    }

    public async Task UpdateNameAsync(int id, string? firstName, string? lastName, CancellationToken ct = default)
    {
        var c = await db.Contacts.SingleAsync(x => x.Id == id, ct);
        c.FirstName = string.IsNullOrWhiteSpace(firstName) ? null : firstName.Trim();
        c.LastName = string.IsNullOrWhiteSpace(lastName) ? null : lastName.Trim();
        c.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> AddToListAsync(IReadOnlyCollection<int> ids, int listId, CancellationToken ct = default)
    {
        var members = (await db.ListContacts.Where(lc => lc.ListId == listId && ids.Contains(lc.ContactId)).Select(lc => lc.ContactId).ToListAsync(ct)).ToHashSet();
        var now = DateTime.UtcNow;
        var added = ids.Where(id => !members.Contains(id)).ToList();
        db.ListContacts.AddRange(added.Select(id => new ListContact { ListId = listId, ContactId = id, AddedAtUtc = now }));
        await db.SaveChangesAsync(ct);
        return added.Count;
    }

    public Task RemoveFromListAsync(int contactId, int listId, CancellationToken ct = default) =>
        db.ListContacts.Where(lc => lc.ContactId == contactId && lc.ListId == listId).ExecuteDeleteAsync(ct);

    // ---------- Lists ----------

    public Task<List<ListSummary>> ListSummariesAsync(CancellationToken ct = default) =>
        db.Lists.AsNoTracking().OrderBy(l => l.Name).Select(l => new ListSummary(
            l.Id, l.Name, l.Description,
            l.ListContacts.Count(),
            l.ListContacts.Count(lc => lc.Contact.Status == ContactStatus.Subscribed && !db.Suppressions.Any(s => s.EmailNormalized == lc.Contact.EmailNormalized)),
            l.CreatedAtUtc)).ToListAsync(ct);

    /// <summary>Creates or renames a list. Returns an error sentence, or null.</summary>
    public async Task<string?> SaveListAsync(int? id, string name, string? description, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) return "Give the list a name.";
        if (await db.Lists.AnyAsync(l => l.Name == name && l.Id != id, ct)) return $"A list named '{name}' already exists.";
        var list = id is null ? db.Lists.Add(new ContactList { CreatedAtUtc = DateTime.UtcNow }).Entity : await db.Lists.SingleAsync(l => l.Id == id, ct);
        list.Name = name;
        list.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Deletes the list (not its contacts). Refused while an unfinished campaign targets it. Returns an error sentence, or null.</summary>
    public async Task<string?> DeleteListAsync(int id, CancellationToken ct = default)
    {
        var inUse = await db.Campaigns.AnyAsync(c => c.ListId == id && c.Status != CampaignStatus.Completed && c.Status != CampaignStatus.Cancelled, ct);
        if (inUse) return "A campaign that hasn't finished uses this list; change or cancel that campaign first.";
        await db.Lists.Where(l => l.Id == id).ExecuteDeleteAsync(ct);
        log.LogInformation("Deleted list {ListId}", id);
        return null;
    }

    // ---------- Suppressions ----------

    /// <summary>Returns an error sentence, or null.</summary>
    public async Task<string?> AddSuppressionAsync(string email, SuppressionReason reason, CancellationToken ct = default)
    {
        var normalized = EmailRules.Normalize(email);
        if (!EmailRules.IsValid(normalized)) return $"'{email}' is not a valid email address.";
        if (await db.Suppressions.AnyAsync(s => s.EmailNormalized == normalized, ct)) return $"{normalized} is already suppressed.";
        db.Suppressions.Add(new Suppression { EmailNormalized = normalized, Reason = reason, Source = "manual", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Suppression added ({Reason})", reason);
        return null;
    }

    /// <summary>Removing a suppression never re-subscribes the contact.</summary>
    public async Task RemoveSuppressionAsync(string emailNormalized, CancellationToken ct = default)
    {
        await db.Suppressions.Where(s => s.EmailNormalized == emailNormalized).ExecuteDeleteAsync(ct);
        log.LogInformation("Suppression removed");
    }
}
