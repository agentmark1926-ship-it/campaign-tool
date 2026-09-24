using System.Collections.Concurrent;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CampaignTool.Tests;

/// <summary>Scripted provider: fails the listed addresses as told, accepts everything else, records every call.</summary>
public class ScriptedSender : IEmailSender
{
    public ConcurrentQueue<OutgoingEmail> Calls { get; } = new();
    public ConcurrentDictionary<string, SendResult> Script { get; } = new();

    public Task<SendResult> SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        Calls.Enqueue(email);
        return Task.FromResult(Script.TryGetValue(email.To, out var r) ? r : SendResult.Sent(Guid.NewGuid().ToString()));
    }
}

public class CampaignSendingTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero));
    private readonly ScriptedSender _sender = new();
    private readonly TestApp _app;

    public CampaignSendingTests()
    {
        _app = new TestApp
        {
            RunWorker = false,
            ConfigureServices = s =>
            {
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(_sender);
            },
        };
        _app.Settings["App:MailingAddress"] = "1101 Brickell Ave, Miami, FL 33131";
        _app.Settings["App:BaseUrl"] = "https://campaigns.example.com";
        _app.Settings["Auth:UnsubscribeKey"] = "test-unsubscribe-key-0123456789abcdef";
        _app.Settings["Acs:SenderAddress"] = "DoNotReply@test.azurecomm.net";
        _app.Settings["Acs:SenderAddresses"] = "DoNotReply@news.example.com,DoNotReply@test.azurecomm.net";
    }

    public Task InitializeAsync() { _app.CreateClient(); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task<int> SeedListAsync(string name, int subscribed, params (string Email, ContactStatus Status)[] extra)
    {
        await using var db = _app.NewDbContext();
        var list = new ContactList { Name = name, CreatedAtUtc = Now };
        db.Lists.Add(list);
        var all = Enumerable.Range(0, subscribed).Select(i => ($"{name.ToLower()}{i}@example.com", ContactStatus.Subscribed)).Concat(extra);
        foreach (var (email, status) in all)
        {
            var existing = await db.Contacts.SingleOrDefaultAsync(c => c.EmailNormalized == email);
            var c = existing ?? db.Contacts.Add(new Contact { Email = email, EmailNormalized = email, Status = status, StatusChangedAtUtc = Now, Source = "test", CreatedAtUtc = Now, UpdatedAtUtc = Now }).Entity;
            db.ListContacts.Add(new ListContact { List = list, Contact = c, AddedAtUtc = Now });
        }
        await db.SaveChangesAsync();
        return list.Id;
    }

    private async Task<int> DraftAsync(int listId, params int[] exclude)
    {
        using var scope = _app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CampaignService>();
        var c = await svc.CreateAsync();
        c.Subject = "Hi {{ first_name | default: \"there\" }}";
        c.Html = "<html><body><p>Hello {{ first_name | default: \"there\" }}</p></body></html>";
        c.ReplyTo = "owner@example.com";
        c.ListId = listId;
        c.ExcludeListIds = System.Text.Json.JsonSerializer.Serialize(exclude);
        Assert.Null(await svc.SaveDraftAsync(c));
        return c.Id;
    }

    private async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _app.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    /// <summary>Runs the worker's send loop against the fake clock until the campaign leaves Sending (or a step limit).</summary>
    private async Task RunUntilDoneAsync(int campaignId, int maxSteps = 5000, Func<Task>? everyStep = null)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var next = await WithAsync(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());
            if (everyStep is not null) await everyStep();
            await using var db = _app.NewDbContext();
            var status = await db.Campaigns.Where(c => c.Id == campaignId).Select(c => c.Status).SingleAsync();
            if (status is CampaignStatus.Completed or CampaignStatus.Cancelled) return;
            _clock.SetUtcNow(next is { } due && due > Now ? due : Now.AddSeconds(1));
        }
        throw new TimeoutException("campaign did not finish");
    }

    [Fact]
    public async Task Materialization_excludes_unsubscribed_bounced_invalid_suppressed_and_excluded_lists()
    {
        var main = await SeedListAsync("Main", 5,
            ("unsub@example.com", ContactStatus.Unsubscribed), ("bounce@example.com", ContactStatus.Bounced),
            ("invalid@example.com", ContactStatus.Invalid), ("supp@example.com", ContactStatus.Subscribed), ("vip@example.com", ContactStatus.Subscribed));
        var vip = await SeedListAsync("Vip", 0, ("vip@example.com", ContactStatus.Subscribed));
        await using (var db = _app.NewDbContext())
        {
            db.Suppressions.Add(new Suppression { EmailNormalized = "supp@example.com", Reason = SuppressionReason.Manual, Source = "test", CreatedAtUtc = Now });
            await db.SaveChangesAsync();
        }
        var id = await DraftAsync(main, vip);

        var sendable = await WithAsync(sp => sp.GetRequiredService<CampaignService>().Sendable(main, [vip]).CountAsync());
        Assert.Null(await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id)));

        await using var check = _app.NewDbContext();
        var campaign = await check.Campaigns.SingleAsync(c => c.Id == id);
        var emails = await check.CampaignRecipients.Where(r => r.CampaignId == id).Select(r => r.EmailSnapshot).ToListAsync();
        Assert.Equal(5, emails.Count);
        Assert.Equal(5, sendable);
        Assert.Equal(5, campaign.Recipients);
        Assert.All(emails, e => Assert.StartsWith("main", e));
        Assert.Equal(CampaignStatus.Sending, campaign.Status);

        // A second insert for the same contact fails on the unique index rather than duplicating.
        var first = await check.CampaignRecipients.FirstAsync(r => r.CampaignId == id);
        check.CampaignRecipients.Add(new CampaignRecipient { CampaignId = id, ContactId = first.ContactId, EmailSnapshot = first.EmailSnapshot });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
    }

    [Fact]
    public async Task Sends_everyone_once_at_the_configured_rate_with_unsubscribe_footer_and_headers()
    {
        var list = await SeedListAsync("Rate", 60);
        var id = await DraftAsync(list);
        var start = Now;
        Assert.Null(await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id)));

        await RunUntilDoneAsync(id);

        var calls = _sender.Calls.ToList();
        Assert.Equal(60, calls.Count);
        Assert.Equal(60, calls.Select(c => c.To).Distinct().Count());
        await using var db = _app.NewDbContext();
        var sentTimes = await db.CampaignRecipients.Where(r => r.CampaignId == id).Select(r => r.SentAtUtc!.Value).ToListAsync();
        foreach (var t in sentTimes) Assert.True(sentTimes.Count(x => x >= t && x < t.AddMinutes(1)) <= 25, "more than 25 in a minute");
        Assert.True(Now - start >= TimeSpan.FromMinutes(2), "60 sends at 25/min need at least two full minutes");

        var campaign = await db.Campaigns.SingleAsync(c => c.Id == id);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.Equal(60, campaign.Sent);

        var one = calls[0];
        Assert.Equal("Hi there", one.Subject);
        Assert.Contains("1101 Brickell Ave, Miami, FL 33131", one.Html);
        Assert.Contains("https://campaigns.example.com/unsubscribe/", one.Html);
        Assert.StartsWith("<https://campaigns.example.com/unsubscribe/", one.Headers!["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", one.Headers["List-Unsubscribe-Post"]);
        Assert.Equal("owner@example.com", one.ReplyTo);
        Assert.Equal("DoNotReply@test.azurecomm.net", one.From);
    }

    [Fact]
    public async Task A_campaign_sends_from_the_address_chosen_on_its_setup_tab_and_rejects_unknown_ones()
    {
        var list = await SeedListAsync("FromPick", 2);
        var id = await DraftAsync(list);
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<CampaignService>();
            var c = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Campaigns.AsNoTracking().SingleAsync(x => x.Id == id);
            c.FromEmail = "someone@not-in-azure.com";
            Assert.Contains("isn't set up in Azure", await svc.SaveDraftAsync(c));
            c.FromEmail = "DoNotReply@news.example.com";
            Assert.Null(await svc.SaveDraftAsync(c));
            Assert.Null(await svc.SendNowAsync(id));
        }

        await RunUntilDoneAsync(id);

        Assert.All(_sender.Calls.Where(e => e.To.StartsWith("frompick")), e => Assert.Equal("DoNotReply@news.example.com", e.From));
    }

    [Fact]
    public async Task Pause_stops_sending_resume_continues_and_cancel_cancels_the_rest()
    {
        var list = await SeedListAsync("Pause", 80);
        var id = await DraftAsync(list);
        await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id));

        await WithAsync(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync()); // first batch of 25
        await WithAsync(async sp => { await sp.GetRequiredService<CampaignService>().PauseAsync(id); return 0; });
        _clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 5; i++) await WithAsync(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());
        Assert.Equal(25, _sender.Calls.Count);

        await WithAsync(async sp => { await sp.GetRequiredService<CampaignService>().ResumeAsync(id); return 0; });
        await WithAsync(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());
        Assert.Equal(50, _sender.Calls.Count);

        await WithAsync(async sp => { await sp.GetRequiredService<CampaignService>().CancelAsync(id); return 0; });
        _clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 5; i++) await WithAsync(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());
        Assert.Equal(50, _sender.Calls.Count);

        await using var db = _app.NewDbContext();
        Assert.Equal(CampaignStatus.Cancelled, (await db.Campaigns.SingleAsync(c => c.Id == id)).Status);
        Assert.Equal(30, await db.CampaignRecipients.CountAsync(r => r.CampaignId == id && r.Status == RecipientStatus.Cancelled));
    }

    [Fact]
    public async Task A_claim_left_by_a_crash_becomes_unknown_and_is_never_resent()
    {
        var list = await SeedListAsync("Crash", 3);
        var id = await DraftAsync(list);
        await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id));
        long crashedId;
        await using (var db = _app.NewDbContext())
        {
            var r = await db.CampaignRecipients.FirstAsync(x => x.CampaignId == id);
            r.Status = RecipientStatus.Claimed;
            r.ClaimedAtUtc = Now.AddMinutes(-11); // the process died mid-send eleven minutes ago
            crashedId = r.Id;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await WithAsync(sp => sp.GetRequiredService<CampaignSender>().RecoverStaleClaimsAsync()));
        await RunUntilDoneAsync(id);

        await using var check = _app.NewDbContext();
        var crashed = await check.CampaignRecipients.SingleAsync(r => r.Id == crashedId);
        Assert.Equal(RecipientStatus.Unknown, crashed.Status);
        Assert.DoesNotContain(_sender.Calls, c => c.To == crashed.EmailSnapshot);
        Assert.Equal(2, _sender.Calls.Count);
        Assert.Equal(CampaignStatus.Completed, (await check.Campaigns.SingleAsync(c => c.Id == id)).Status);
    }

    [Fact]
    public async Task Transient_failures_follow_the_backoff_then_fail_and_a_rejected_address_becomes_invalid()
    {
        var list = await SeedListAsync("Fail", 0, ("busy@example.com", ContactStatus.Subscribed), ("bad@example.com", ContactStatus.Subscribed), ("ok@example.com", ContactStatus.Subscribed));
        _sender.Script["busy@example.com"] = SendResult.TransientFailure("429 TooManyRequests");
        _sender.Script["bad@example.com"] = SendResult.PermanentFailure("400 InvalidRecipient");
        var id = await DraftAsync(list);
        await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id));

        await RunUntilDoneAsync(id);

        Assert.Equal(6, _sender.Calls.Count(c => c.To == "busy@example.com")); // first send + five retries
        Assert.Equal(1, _sender.Calls.Count(c => c.To == "bad@example.com"));
        await using var db = _app.NewDbContext();
        var busy = await db.CampaignRecipients.SingleAsync(r => r.CampaignId == id && r.EmailSnapshot == "busy@example.com");
        Assert.Equal(RecipientStatus.Failed, busy.Status);
        Assert.Equal(6, busy.AttemptCount);
        Assert.Equal(ContactStatus.Invalid, (await db.Contacts.SingleAsync(c => c.EmailNormalized == "bad@example.com")).Status);
        Assert.Equal(ContactStatus.Subscribed, (await db.Contacts.SingleAsync(c => c.EmailNormalized == "busy@example.com")).Status);
        var campaign = await db.Campaigns.SingleAsync(c => c.Id == id);
        Assert.Equal((1, 2), (campaign.Sent, campaign.Failed));
    }

    [Fact]
    public async Task Someone_who_unsubscribes_after_scheduling_is_not_sent_to()
    {
        var list = await SeedListAsync("Sched", 2);
        var id = await DraftAsync(list);
        Assert.Null(await WithAsync(sp => sp.GetRequiredService<CampaignService>().ScheduleAsync(id, Now.AddHours(1))));

        await using (var db = _app.NewDbContext())
        {
            var c = await db.Contacts.SingleAsync(x => x.EmailNormalized == "sched0@example.com");
            c.Status = ContactStatus.Unsubscribed;
            await db.SaveChangesAsync();
        }

        await WithAsync(async sp => { await sp.GetRequiredService<CampaignSender>().StartDueScheduledAsync(); return 0; });
        await using (var db = _app.NewDbContext())
            Assert.Equal(CampaignStatus.Scheduled, (await db.Campaigns.SingleAsync(c => c.Id == id)).Status); // not yet due

        _clock.Advance(TimeSpan.FromHours(1));
        await WithAsync(async sp => { await sp.GetRequiredService<CampaignSender>().StartDueScheduledAsync(); return 0; });
        await RunUntilDoneAsync(id);

        Assert.Equal(["sched1@example.com"], _sender.Calls.Select(c => c.To));
        await using var check = _app.NewDbContext();
        Assert.Equal(RecipientStatus.Cancelled, (await check.CampaignRecipients.SingleAsync(r => r.CampaignId == id && r.EmailSnapshot == "sched0@example.com")).Status);
    }

    [Fact]
    public async Task Send_is_refused_without_a_mailing_address_or_subject()
    {
        var list = await SeedListAsync("Refuse", 1);
        var id = await DraftAsync(list);
        await using (var db = _app.NewDbContext())
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == id);
            c.Subject = "";
            await db.SaveChangesAsync();
        }
        Assert.Equal("Add a subject line.", await WithAsync(sp => sp.GetRequiredService<CampaignService>().SendNowAsync(id)));
        Assert.Empty(_sender.Calls);
    }
}
