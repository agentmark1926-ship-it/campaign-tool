using System.Text.Json;
using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

public record ImportPreview(int TotalRows, int Valid, int Invalid, int Duplicates);

public record ImportReject(int Row, string Value, string Reason);

/// <summary>
/// Upload → Detect → Map → Preview → Confirm (queue) → the CampaignWorker processes it in batches of 1,000.
/// An import never changes a contact's Status.
/// </summary>
public class ContactImportService(AppDbContext db, ImportFileStore files, IOptions<ImportOptions> options, ILogger<ContactImportService> log)
{
    public const int BatchSize = 1000;
    public const long MaxFileBytes = 50 * 1024 * 1024;
    public const string Email = "email", FirstName = "first_name", LastName = "last_name", Ignore = "ignore", CustomPrefix = "custom:";

    public async Task<(Import Import, CsvLayout Layout)> CreateAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".csv" or ".txt"))
            throw new ArgumentException("Upload a .csv or .txt file.");

        var import = new Import { FileName = Path.GetFileName(fileName), CreatedAtUtc = DateTime.UtcNow };
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);

        import.BlobPath = await files.SaveAsync(import.Id, fileName, content, ct);
        await using var stream = await files.OpenReadAsync(import.BlobPath, ct);
        var layout = CsvDetector.Detect(stream);
        import.Delimiter = layout.Delimiter;
        import.HasHeader = layout.HasHeader;
        import.MappingJson = JsonSerializer.Serialize(DefaultMapping(layout));
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import {ImportId} uploaded: {Columns} column(s), header {HasHeader}", import.Id, layout.Columns.Count, layout.HasHeader);
        return (import, layout);
    }

    public static List<string> DefaultMapping(CsvLayout layout) =>
        layout.Columns.Select((name, i) =>
        {
            if (i == layout.EmailColumn) return Email;
            var key = name.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (key is "firstname" or "first" or "givenname") return FirstName;
            if (key is "lastname" or "last" or "surname" or "familyname") return LastName;
            return layout.HasHeader ? CustomPrefix + name : Ignore;
        }).ToList();

    public static string? ValidateMapping(IReadOnlyList<string> mapping) =>
        mapping.Count(m => m == Email) == 1 ? null : "Map exactly one column to Email.";

    public async Task<ImportPreview> PreviewAsync(Import import, IReadOnlyList<string> mapping, CancellationToken ct = default)
    {
        var emailIndex = IndexOf(mapping, Email);
        var seen = new HashSet<string>();
        int total = 0, valid = 0, invalid = 0, duplicates = 0;
        await using var stream = await files.OpenReadAsync(import.BlobPath, ct);
        foreach (var record in CsvDetector.Records(stream, import.Delimiter).Skip(import.HasHeader ? 1 : 0))
        {
            total++;
            var email = EmailRules.Normalize(At(record, emailIndex));
            if (!EmailRules.IsValid(email)) invalid++;
            else if (!seen.Add(email)) duplicates++;
            else valid++;
        }
        return new ImportPreview(total, valid, invalid, duplicates);
    }

    /// <summary>Saves the mapping and list choice and hands the import to the worker. Returns an error sentence, or null.</summary>
    public async Task<string?> QueueAsync(Import import, IReadOnlyList<string> mapping, int? listId, string? newListName, CancellationToken ct = default)
    {
        if (ValidateMapping(mapping) is { } error) return error;
        if (!string.IsNullOrWhiteSpace(newListName))
        {
            var name = newListName.Trim();
            if (await db.Lists.AnyAsync(l => l.Name == name, ct)) return $"A list named '{name}' already exists; pick it instead.";
            var list = new ContactList { Name = name, CreatedAtUtc = DateTime.UtcNow };
            db.Lists.Add(list);
            await db.SaveChangesAsync(ct);
            listId = list.Id;
        }
        import.MappingJson = JsonSerializer.Serialize(mapping);
        import.ListId = listId;
        import.Status = ImportStatus.Queued;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import {ImportId} queued, list {ListId}", import.Id, listId);
        return null;
    }

    /// <summary>Called by the CampaignWorker. Claims the oldest queued import atomically and processes it. Returns false when there was nothing to do.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var id = await db.Imports.Where(i => i.Status == ImportStatus.Queued).OrderBy(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        if (id is null) return false;
        var claimed = await db.Imports.Where(i => i.Id == id && i.Status == ImportStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, ImportStatus.Processing), ct);
        if (claimed == 0) return true;

        var import = await db.Imports.SingleAsync(i => i.Id == id, ct);
        try
        {
            await ProcessAsync(import, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Import {ImportId} failed", id);
            db.ChangeTracker.Clear();
            await db.Imports.Where(i => i.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, ImportStatus.Failed)
                .SetProperty(i => i.CompletedAtUtc, DateTime.UtcNow)
                .SetProperty(i => i.ErrorsJson, JsonSerializer.Serialize(new[] { new ImportReject(0, "", $"Import stopped: {ex.Message}") })), ct);
        }
        return true;
    }

    private async Task ProcessAsync(Import import, CancellationToken ct)
    {
        var mapping = JsonSerializer.Deserialize<List<string>>(import.MappingJson) ?? [];
        var emailIndex = IndexOf(mapping, Email);
        var seen = new HashSet<string>();
        var rejects = new List<ImportReject>();
        var batch = new List<Row>(BatchSize);
        var rowNumber = 0;

        await using var stream = await files.OpenReadAsync(import.BlobPath, ct);
        foreach (var record in CsvDetector.Records(stream, import.Delimiter))
        {
            rowNumber++;
            if (import.HasHeader && rowNumber == 1) continue;
            import.TotalRows++;

            var raw = At(record, emailIndex);
            var email = EmailRules.Normalize(raw);
            if (!EmailRules.IsValid(email))
            {
                import.Invalid++;
                rejects.Add(new ImportReject(rowNumber, raw, string.IsNullOrWhiteSpace(raw) ? "Email is empty" : "Not a valid email address"));
                continue;
            }
            if (!seen.Add(email))
            {
                import.Skipped++;
                rejects.Add(new ImportReject(rowNumber, raw, "Duplicate of an earlier row in this file"));
                continue;
            }
            batch.Add(ToRow(rowNumber, record, mapping, email));
            if (batch.Count == BatchSize)
            {
                await ApplyBatchAsync(import, batch, rejects, ct);
                batch.Clear();
            }
        }
        await ApplyBatchAsync(import, batch, rejects, ct);

        import.Status = ImportStatus.Completed;
        import.CompletedAtUtc = DateTime.UtcNow;
        import.ErrorsJson = JsonSerializer.Serialize(rejects);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Import {ImportId} completed: {Total} rows, {Imported} imported, {Updated} updated, {Skipped} skipped, {Invalid} invalid",
            import.Id, import.TotalRows, import.Imported, import.Updated, import.Skipped, import.Invalid);
    }

    private async Task ApplyBatchAsync(Import import, List<Row> batch, List<ImportReject> rejects, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var emails = batch.Select(r => r.Email).ToList();
        var existing = await db.Contacts.Where(c => emails.Contains(c.EmailNormalized)).ToDictionaryAsync(c => c.EmailNormalized, ct);
        var suppressed = await db.Suppressions.Where(s => emails.Contains(s.EmailNormalized)).ToDictionaryAsync(s => s.EmailNormalized, s => s.Reason, ct);
        var forList = new List<Contact>();

        foreach (var row in batch)
        {
            if (existing.TryGetValue(row.Email, out var contact))
            {
                if (!options.Value.UpdateExisting)
                {
                    import.Skipped++;
                    rejects.Add(new ImportReject(row.Number, row.Email, "Already a contact (updates are turned off)"));
                    continue;
                }
                // Merge non-empty fields only; Status is never touched by an import.
                if (!string.IsNullOrWhiteSpace(row.FirstName)) contact.FirstName = row.FirstName;
                if (!string.IsNullOrWhiteSpace(row.LastName)) contact.LastName = row.LastName;
                if (row.Custom.Count > 0) contact.CustomFields = MergeCustom(contact.CustomFields, row.Custom);
                contact.UpdatedAtUtc = now;
                import.Updated++;
            }
            else
            {
                // A suppressed address is imported for history but keeps the status its suppression implies.
                var (status, reason) = suppressed.TryGetValue(row.Email, out var why) ? StatusFor(why) : (ContactStatus.Subscribed, (string?)null);
                contact = new Contact
                {
                    Email = row.Email,
                    EmailNormalized = row.Email,
                    FirstName = row.FirstName,
                    LastName = row.LastName,
                    CustomFields = MergeCustom("{}", row.Custom),
                    Status = status,
                    StatusReason = reason,
                    StatusChangedAtUtc = now,
                    Source = "import",
                    ImportId = import.Id,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.Contacts.Add(contact);
                import.Imported++;
            }
            forList.Add(contact);
        }

        if (import.ListId is int listId && forList.Count > 0)
        {
            var ids = forList.Where(c => c.Id != 0).Select(c => c.Id).ToList();
            var members = (await db.ListContacts.Where(lc => lc.ListId == listId && ids.Contains(lc.ContactId)).Select(lc => lc.ContactId).ToListAsync(ct)).ToHashSet();
            foreach (var c in forList.Where(c => c.Id == 0 || !members.Contains(c.Id)))
                db.ListContacts.Add(new ListContact { ListId = listId, Contact = c, AddedAtUtc = now });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        db.Attach(import);
    }

    public static (ContactStatus, string) StatusFor(SuppressionReason reason) => reason switch
    {
        SuppressionReason.HardBounce => (ContactStatus.Bounced, "Suppressed: hard bounce"),
        SuppressionReason.Complaint => (ContactStatus.Complained, "Suppressed: complaint"),
        SuppressionReason.Unsubscribe => (ContactStatus.Unsubscribed, "Suppressed: unsubscribed"),
        _ => (ContactStatus.Unsubscribed, "Suppressed: manual"),
    };

    private record Row(int Number, string Email, string? FirstName, string? LastName, Dictionary<string, string> Custom);

    private static Row ToRow(int number, string[] record, List<string> mapping, string email)
    {
        string? first = null, last = null;
        var custom = new Dictionary<string, string>();
        for (var i = 0; i < mapping.Count; i++)
        {
            var value = At(record, i).Trim();
            if (value.Length == 0) continue;
            if (mapping[i] == FirstName) first = Truncate(value, 100);
            else if (mapping[i] == LastName) last = Truncate(value, 100);
            else if (mapping[i].StartsWith(CustomPrefix)) custom[mapping[i][CustomPrefix.Length..]] = value;
        }
        return new Row(number, email, first, last, custom);
    }

    private static string MergeCustom(string json, Dictionary<string, string> values)
    {
        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? [];
        foreach (var (k, v) in values) merged[k] = v;
        return JsonSerializer.Serialize(merged);
    }

    private static int IndexOf(IReadOnlyList<string> mapping, string value)
    {
        for (var i = 0; i < mapping.Count; i++) if (mapping[i] == value) return i;
        return -1;
    }

    private static string At(string[] record, int index) => index >= 0 && index < record.Length ? record[index] : "";

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
