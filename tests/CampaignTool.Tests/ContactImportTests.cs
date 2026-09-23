using System.Diagnostics;
using System.Text;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CampaignTool.Tests;

public class ContactImportTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public Task InitializeAsync() { _app.CreateClient(); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Upload, keep the detected mapping, queue, and wait for the worker (or this call) to finish.</summary>
    private async Task<Import> RunImportAsync(string text, string fileName = "list.csv", string? newList = null, Func<List<string>, List<string>>? remap = null)
    {
        int id;
        using (var scope = _app.Services.CreateScope())
        {
            var imports = scope.ServiceProvider.GetRequiredService<ContactImportService>();
            var (import, layout) = await imports.CreateAsync(fileName, new MemoryStream(Encoding.UTF8.GetBytes(text)));
            var mapping = ContactImportService.DefaultMapping(layout);
            Assert.Null(await imports.QueueAsync(import, remap?.Invoke(mapping) ?? mapping, null, newList));
            id = import.Id;
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(2))
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ContactImportService>().ProcessNextAsync();
            await using var db = _app.NewDbContext();
            var import = await db.Imports.SingleAsync(i => i.Id == id);
            if (import.Status is ImportStatus.Completed or ImportStatus.Failed) return import;
            await Task.Delay(200);
        }
        throw new TimeoutException("Import did not finish within 2 minutes.");
    }

    [Fact]
    public async Task Ten_thousand_row_one_column_file_imports_in_under_two_minutes_with_correct_counts()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 9_900; i++) sb.Append($"user{i}@example.com\n");
        for (var i = 0; i < 60; i++) sb.Append($"USER{i}@Example.com\n");   // duplicates after normalization
        for (var i = 0; i < 40; i++) sb.Append($"not-an-email-{i}\n");       // invalid

        var sw = Stopwatch.StartNew();
        var import = await RunImportAsync(sb.ToString(), "subscribers.txt");
        sw.Stop();

        Assert.Equal(ImportStatus.Completed, import.Status);
        Assert.False(import.HasHeader);
        Assert.Equal(10_000, import.TotalRows);
        Assert.Equal(9_900, import.Imported);
        Assert.Equal(60, import.Skipped);
        Assert.Equal(40, import.Invalid);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(2), $"took {sw.Elapsed}");

        await using var db = _app.NewDbContext();
        Assert.Equal(9_900, await db.Contacts.CountAsync(c => c.Status == ContactStatus.Subscribed));
    }

    [Fact]
    public async Task Reimport_updates_fields_but_never_changes_status()
    {
        await using (var db = _app.NewDbContext())
        {
            var now = DateTime.UtcNow;
            db.Contacts.Add(new Contact { Email = "ann@example.com", EmailNormalized = "ann@example.com", FirstName = "Old", LastName = "Keep", Status = ContactStatus.Unsubscribed, StatusReason = "clicked unsubscribe", StatusChangedAtUtc = now, Source = "test", CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
        }

        var import = await RunImportAsync("email,first_name,last_name\nANN@example.com,Ann,\nbo@example.com,Bo,Kim\n");

        Assert.Equal(1, import.Imported);
        Assert.Equal(1, import.Updated);
        await using var check = _app.NewDbContext();
        var ann = await check.Contacts.SingleAsync(c => c.EmailNormalized == "ann@example.com");
        Assert.Equal("Ann", ann.FirstName);          // non-empty value replaced
        Assert.Equal("Keep", ann.LastName);          // empty value did not erase
        Assert.Equal(ContactStatus.Unsubscribed, ann.Status);
        Assert.Equal("clicked unsubscribe", ann.StatusReason);
    }

    [Fact]
    public async Task Suppressed_address_is_imported_but_not_subscribed()
    {
        await using (var db = _app.NewDbContext())
        {
            db.Suppressions.Add(new Suppression { EmailNormalized = "gone@example.com", Reason = SuppressionReason.HardBounce, Source = "test", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await RunImportAsync("gone@example.com\nhere@example.com\n");

        await using var check = _app.NewDbContext();
        Assert.Equal(ContactStatus.Bounced, (await check.Contacts.SingleAsync(c => c.EmailNormalized == "gone@example.com")).Status);
        Assert.Equal(ContactStatus.Subscribed, (await check.Contacts.SingleAsync(c => c.EmailNormalized == "here@example.com")).Status);
    }

    [Fact]
    public async Task Multi_column_file_maps_custom_fields_and_adds_everyone_to_a_new_list()
    {
        var import = await RunImportAsync("Name;Email;Facility\nAnn Lee;ann@example.com;Miami\n\"Kim; Bo\";bo@example.com;Tampa\n", newList: "Florida",
            remap: m => { m[0] = ContactImportService.FirstName; return m; });

        Assert.Equal(2, import.Imported);
        await using var db = _app.NewDbContext();
        var list = await db.Lists.Include(l => l.ListContacts).SingleAsync(l => l.Name == "Florida");
        Assert.Equal(2, list.ListContacts.Count);
        var bo = await db.Contacts.SingleAsync(c => c.EmailNormalized == "bo@example.com");
        Assert.Equal("Kim; Bo", bo.FirstName);
        Assert.Contains("\"Facility\":\"Tampa\"", bo.CustomFields);
    }

    [Fact]
    public async Task Rejects_record_row_numbers_and_reasons()
    {
        var import = await RunImportAsync("email\nok@example.com\nbad\nok@example.com\n");
        Assert.Contains("\"Row\":3", import.ErrorsJson);
        Assert.Contains("Not a valid email address", import.ErrorsJson);
        Assert.Contains("\"Row\":4", import.ErrorsJson);
        Assert.Contains("Duplicate", import.ErrorsJson);
    }

    [Fact]
    public async Task Unsubscribe_suppresses_and_resubscribe_is_refused_until_suppression_removed()
    {
        await RunImportAsync("ann@example.com\n");
        int id;
        await using (var db = _app.NewDbContext()) id = (await db.Contacts.SingleAsync()).Id;

        using var scope = _app.Services.CreateScope();
        var contacts = scope.ServiceProvider.GetRequiredService<ContactService>();
        await contacts.UnsubscribeAsync([id], "Unsubscribed by owner");
        Assert.NotNull(await contacts.ResubscribeAsync(id));

        await contacts.RemoveSuppressionAsync("ann@example.com");
        await using (var db = _app.NewDbContext())
            Assert.Equal(ContactStatus.Unsubscribed, (await db.Contacts.SingleAsync()).Status); // removal alone never re-subscribes

        Assert.Null(await contacts.ResubscribeAsync(id));
        await using (var db = _app.NewDbContext())
            Assert.Equal(ContactStatus.Subscribed, (await db.Contacts.SingleAsync()).Status);
    }

    [Fact]
    public async Task List_sendable_count_excludes_unsubscribed_and_suppressed()
    {
        await RunImportAsync("a@example.com\nb@example.com\nc@example.com\n", newList: "All");
        await using (var db = _app.NewDbContext())
        {
            var b = await db.Contacts.SingleAsync(c => c.EmailNormalized == "b@example.com");
            b.Status = ContactStatus.Unsubscribed;
            db.Suppressions.Add(new Suppression { EmailNormalized = "c@example.com", Reason = SuppressionReason.Manual, Source = "test", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var scope = _app.Services.CreateScope();
        var summary = (await scope.ServiceProvider.GetRequiredService<ContactService>().ListSummariesAsync()).Single(l => l.Name == "All");
        Assert.Equal(3, summary.Members);
        Assert.Equal(1, summary.Sendable);
    }
}
