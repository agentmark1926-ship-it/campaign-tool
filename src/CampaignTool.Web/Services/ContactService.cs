using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Web.Services;

public record ContactQuery(string? Search = null, ContactStatus? Status = null, int? ListId = null);

/// <summary>What a paste did: the list it filled and what happened to each address.</summary>
public record PasteResult(int ListId, string ListName, int Added, int AlreadyContacts, int NotSubscribed, IReadOnlyList<string> Invalid);

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

    public record PastedAddress(string Email, string? FirstName, string? LastName);

    /// <summary>
    /// Pulls every address out of pasted text, with a name when one is given: "Jane Smith &lt;jane@x.com&gt;", "Jane Smith, jane@x.com",
    /// or Excel columns (First, Last, Email). Addresses can also be separated by commas, semicolons, tabs, spaces or new lines.
    /// </summary>
    public static (List<PastedAddress> Valid, List<string> Invalid) ParsePasted(string text)
    {
        var valid = new List<PastedAddress>();
        var invalid = new List<string>();
        var seen = new HashSet<string>();
        foreach (var line in (text ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pendingName = new List<string>(); // cells before the address on this line (Excel: First, Last, Email)
            foreach (var cell in line.Split([',', ';', '\t']))
            {
                var words = cell.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var emails = words.Where(w => w.Contains('@')).ToList();
                var nameWords = words.Where(w => !w.Contains('@')).Select(w => w.Trim('"', '\'', '<', '>', '(', ')', '[', ']')).Where(w => w.Length > 0).ToList();
                if (emails.Count == 0)
                {
                    if (nameWords.Count > 0) pendingName.Add(string.Join(' ', nameWords));
                    continue;
                }
                var name = emails.Count > 1 ? [] : nameWords.Count > 0 ? nameWords : pendingName.SelectMany(n => n.Split(' ')).ToList();
                pendingName.Clear();
                foreach (var raw in emails)
                {
                    var token = raw.Trim('<', '>', '"', '\'', '(', ')', '[', ']');
                    if (token.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) token = token[7..];
                    var email = EmailRules.Normalize(token);
                    if (!seen.Add(email)) continue;
                    if (!EmailRules.IsValid(email)) { invalid.Add(email); continue; }
                    valid.Add(new PastedAddress(email, name.Count > 0 ? Title(name[0]) : null, name.Count > 1 ? Title(string.Join(' ', name.Skip(1))) : null));
                }
            }
        }
        return (valid, invalid);
    }

    // "JANE" or "jane" → "Jane"; mixed case ("McDonald") is kept as typed.
    private static string Title(string s) =>
        s.Any(char.IsLower) && s.Any(char.IsUpper) ? s : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    /// <summary>
    /// Adds pasted addresses as contacts in a new list. New addresses become Subscribed (the owner confirms consent first);
    /// existing contacts keep their status, so anyone who unsubscribed or bounced stays excluded.
    /// </summary>
    public async Task<PasteResult> AddPastedAsync(string text, string listName, CancellationToken ct = default)
    {
        var (valid, invalid) = ParsePasted(text);
        var now = DateTime.UtcNow;
        var name = listName.Trim();
        for (var i = 2; await db.Lists.AnyAsync(l => l.Name == name, ct); i++) name = $"{listName.Trim()} ({i})";
        var list = db.Lists.Add(new ContactList { Name = name, Description = "Pasted addresses", CreatedAtUtc = now }).Entity;

        var emails = valid.Select(v => v.Email).ToList();
        var existing = await db.Contacts.Where(c => emails.Contains(c.EmailNormalized)).ToDictionaryAsync(c => c.EmailNormalized, ct);
        var added = 0;
        foreach (var (email, first, last) in valid)
        {
            if (!existing.TryGetValue(email, out var contact))
            {
                contact = db.Contacts.Add(new Contact
                {
                    Email = email, EmailNormalized = email, FirstName = first, LastName = last, Status = ContactStatus.Subscribed, StatusChangedAtUtc = now,
                    Source = "pasted", CreatedAtUtc = now, UpdatedAtUtc = now,
                }).Entity;
                added++;
            }
            else if (string.IsNullOrWhiteSpace(contact.FirstName) && string.IsNullOrWhiteSpace(contact.LastName) && first is not null)
            {
                // Fill in a name we didn't have; never overwrite one already on file.
                contact.FirstName = first;
                contact.LastName = last;
                contact.UpdatedAtUtc = now;
            }
            db.ListContacts.Add(new ListContact { List = list, Contact = contact, AddedAtUtc = now });
        }
        await db.SaveChangesAsync(ct);
        var notSubscribed = existing.Values.Count(c => c.Status != ContactStatus.Subscribed);
        log.LogInformation("Pasted {Count} address(es) into list {ListId}: {Added} new, {Invalid} invalid", valid.Count, list.Id, added, invalid.Count);
        return new PasteResult(list.Id, name, added, existing.Count, notSubscribed, invalid);
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
