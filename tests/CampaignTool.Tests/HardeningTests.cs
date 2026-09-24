using System.Text;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CampaignTool.Tests;

public class HardeningTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 16, 0, 0, TimeSpan.Zero));
    private readonly TestApp _app;

    public HardeningTests()
    {
        _app = new TestApp
        {
            RunWorker = false,
            ConfigureServices = s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock); },
        };
    }

    public Task InitializeAsync() { _app.CreateClient(); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _app.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private async Task<(int CampaignId, int ContactId)> CampaignAsync(CampaignStatus status, int recipients, int failed, DateTime? lastBatch = null)
    {
        await using var db = _app.NewDbContext();
        var contact = new Contact { Email = $"{Guid.NewGuid():N}@example.com", StatusChangedAtUtc = Now, Source = "t", CreatedAtUtc = Now, UpdatedAtUtc = Now };
        contact.EmailNormalized = contact.Email;
        var c = new Campaign { Name = "C", Status = status, Recipients = recipients, Failed = failed, StartedAtUtc = Now.AddHours(-2), LastBatchAtUtc = lastBatch, CreatedAtUtc = Now,
            CompletedAtUtc = status == CampaignStatus.Completed ? Now.AddHours(-1) : null };
        db.AddRange(contact, c);
        await db.SaveChangesAsync();
        return (c.Id, contact.Id);
    }

    [Fact]
    public async Task Retention_deletes_old_events_and_old_import_files_but_keeps_counters()
    {
        var (campaignId, _) = await CampaignAsync(CampaignStatus.Completed, 10, 0);
        await using (var db = _app.NewDbContext())
        {
            await db.Campaigns.Where(c => c.Id == campaignId).ExecuteUpdateAsync(u => u.SetProperty(c => c.Delivered, 9));
            db.EmailEvents.AddRange(
                new EmailEvent { EventId = "old", AcsMessageId = "m", Kind = EmailEventKind.Delivery, Status = "Delivered", OccurredAtUtc = Now.AddMonths(-13), RawJson = "{}" },
                new EmailEvent { EventId = "new", AcsMessageId = "m", Kind = EmailEventKind.Delivery, Status = "Delivered", OccurredAtUtc = Now.AddMonths(-11), RawJson = "{}" });
            await db.SaveChangesAsync();
        }
        var files = _app.Services.GetRequiredService<ImportFileStore>();
        var oldPath = await files.SaveAsync(90001, "old.csv", new MemoryStream(Encoding.UTF8.GetBytes("a@example.com")));
        var newPath = await files.SaveAsync(90002, "new.csv", new MemoryStream(Encoding.UTF8.GetBytes("b@example.com")));
        await using (var db = _app.NewDbContext())
        {
            db.Imports.AddRange(
                new Import { FileName = "old.csv", BlobPath = oldPath, CreatedAtUtc = Now.AddDays(-31) },
                new Import { FileName = "new.csv", BlobPath = newPath, CreatedAtUtc = Now.AddDays(-29) });
            await db.SaveChangesAsync();
        }

        var result = await With(sp => sp.GetRequiredService<RetentionService>().RunAsync());

        Assert.Equal((1, 1), (result.EventsDeleted, result.FilesDeleted));
        await using var check = _app.NewDbContext();
        Assert.Equal(["new"], await check.EmailEvents.Select(e => e.EventId).ToListAsync());
        Assert.Equal(9, (await check.Campaigns.SingleAsync(c => c.Id == campaignId)).Delivered);
        Assert.Equal("", (await check.Imports.SingleAsync(i => i.FileName == "old.csv")).BlobPath);
        Assert.Equal(newPath, (await check.Imports.SingleAsync(i => i.FileName == "new.csv")).BlobPath);
        await Assert.ThrowsAnyAsync<IOException>(() => files.OpenReadAsync(oldPath));
        await using var stillThere = await files.OpenReadAsync(newPath);
    }

    [Fact]
    public async Task Alerts_fire_for_failed_share_and_unknown_rows_but_not_for_healthy_campaigns()
    {
        await CampaignAsync(CampaignStatus.Completed, 100, 1);                  // 1%: fine
        var (bad, contactId) = await CampaignAsync(CampaignStatus.Completed, 100, 3);   // 3%: alert
        await using (var db = _app.NewDbContext())
        {
            db.CampaignRecipients.Add(new CampaignRecipient { CampaignId = bad, ContactId = contactId, EmailSnapshot = "x@example.com", Status = RecipientStatus.Unknown });
            await db.SaveChangesAsync();
        }

        var alerts = await With(sp => sp.GetRequiredService<AlertMonitor>().CheckAsync());

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Contains($"Campaign {bad}") && a.Contains("3%"));
        Assert.Contains(alerts, a => a.Contains("1 recipient(s) are Unknown"));
    }

    [Fact]
    public async Task Stall_alert_fires_only_when_work_is_due_and_the_rate_limit_allows_sending()
    {
        var (id, contactId) = await CampaignAsync(CampaignStatus.Sending, 10, 0, lastBatch: Now.AddMinutes(-31));
        await using (var db = _app.NewDbContext())
        {
            db.CampaignRecipients.Add(new CampaignRecipient { CampaignId = id, ContactId = contactId, EmailSnapshot = "p@example.com", Status = RecipientStatus.Pending });
            await db.SaveChangesAsync();
        }
        var limiter = _app.Services.GetRequiredService<RateLimiter>();

        Assert.Contains(await With(sp => sp.GetRequiredService<AlertMonitor>().CheckAsync()), a => a.Contains("no batch for over 30 minutes"));

        // Waiting out the hourly limit is normal, not a stall.
        for (var i = 0; i < 90; i++) limiter.Record(Now.AddMinutes(-20));
        Assert.DoesNotContain(await With(sp => sp.GetRequiredService<AlertMonitor>().CheckAsync()), a => a.Contains("no batch"));
    }
}
