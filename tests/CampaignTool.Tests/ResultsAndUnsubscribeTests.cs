using System.Net;
using System.Net.Http.Json;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CampaignTool.Tests;

public class ResultsAndUnsubscribeTests : IAsyncLifetime
{
    private const string Key = "webhook-key-for-results-tests-0123456789";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 24, 15, 0, 0, TimeSpan.Zero));
    private readonly ScriptedSender _sender = new();
    private readonly TestApp _app;
    private HttpClient _http = null!;

    public ResultsAndUnsubscribeTests()
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
        _app.Settings["Webhooks:AcsSecret"] = Key;
    }

    public Task InitializeAsync() { _http = _app.CreateClient(new() { AllowAutoRedirect = false }); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A list of n subscribed contacts, a campaign to it, sent (all rows Sent, campaign still Sending until the next pass).</summary>
    private async Task<(int CampaignId, int ListId, List<CampaignRecipient> Rows)> SentCampaignAsync(int n, bool finish = true)
    {
        int listId;
        await using (var db = _app.NewDbContext())
        {
            var list = new ContactList { Name = $"L{Guid.NewGuid():N}", CreatedAtUtc = Now };
            db.Lists.Add(list);
            for (var i = 0; i < n; i++)
            {
                var email = $"r{i}-{list.Name}@example.com".ToLower();
                db.ListContacts.Add(new ListContact { List = list, AddedAtUtc = Now, Contact = new Contact { Email = email, EmailNormalized = email, Status = ContactStatus.Subscribed, StatusChangedAtUtc = Now, Source = "test", CreatedAtUtc = Now, UpdatedAtUtc = Now } });
            }
            await db.SaveChangesAsync();
            listId = list.Id;
        }
        using var scope = _app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CampaignService>();
        var c = await svc.CreateAsync();
        c.Subject = "Hello"; c.Html = "<p>Hi</p>"; c.ReplyTo = "owner@example.com"; c.ListId = listId;
        Assert.Null(await svc.SaveDraftAsync(c));
        Assert.Null(await svc.SendNowAsync(c.Id));
        var sender = scope.ServiceProvider.GetRequiredService<CampaignSender>();
        await sender.SendBatchAsync();
        if (finish) await sender.SendBatchAsync();
        await using var check = _app.NewDbContext();
        return (c.Id, listId, await check.CampaignRecipients.Where(r => r.CampaignId == c.Id).OrderBy(r => r.Id).ToListAsync());
    }

    private Task<HttpResponseMessage> Delivery(string eventId, string messageId, string status) =>
        _http.PostAsJsonAsync($"/webhooks/acs?key={Key}", new[]
        {
            new { id = eventId, eventType = EventProcessor.DeliveryReport, eventTime = "2026-09-24T15:01:00Z",
                  data = new { messageId, status, deliveryAttemptTimeStamp = "2026-09-24T15:01:00Z", deliveryStatusDetails = new { statusMessage = $"{status} detail" } } },
        });

    private Task<HttpResponseMessage> Click(string eventId, string messageId, string url) =>
        _http.PostAsJsonAsync($"/webhooks/acs?key={Key}", new[]
        {
            new { id = eventId, eventType = EventProcessor.EngagementReport, eventTime = "2026-09-24T15:05:00Z",
                  data = new { messageId, engagementType = "click", engagementContext = url, userActionTimeStamp = "2026-09-24T15:05:00Z" } },
        });

    [Fact]
    public async Task Delivery_statuses_update_recipients_counters_and_contacts_once()
    {
        var (id, _, rows) = await SentCampaignAsync(4);
        await Delivery("e1", rows[0].AcsMessageId!, "Delivered");
        await Delivery("e1", rows[0].AcsMessageId!, "Delivered");          // duplicate EventId: ignored
        await Delivery("e2", rows[1].AcsMessageId!, "Bounced");
        await Delivery("e3", rows[2].AcsMessageId!, "Suppressed");
        await Delivery("e4", rows[3].AcsMessageId!, "Quarantined");
        await Delivery("e5", rows[0].AcsMessageId!, "Bounced");            // late contradictory event can't undo Delivered

        await using var db = _app.NewDbContext();
        var r = await db.CampaignRecipients.Where(x => x.CampaignId == id).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal([RecipientStatus.Delivered, RecipientStatus.Bounced, RecipientStatus.Failed, RecipientStatus.Failed], r.Select(x => x.Status));
        Assert.NotNull(r[0].DeliveredAtUtc);
        Assert.Equal("Quarantined", r[3].LastError);
        var c = await db.Campaigns.SingleAsync(x => x.Id == id);
        Assert.Equal((4, 1, 1, 2), (c.Sent, c.Delivered, c.Bounced, c.Failed));

        var bounced = await db.Contacts.SingleAsync(x => x.Id == rows[1].ContactId);
        Assert.Equal(ContactStatus.Bounced, bounced.Status);
        Assert.True(await db.Suppressions.AnyAsync(s => s.EmailNormalized == bounced.EmailNormalized && s.Reason == SuppressionReason.HardBounce));
        Assert.Equal(ContactStatus.Bounced, (await db.Contacts.SingleAsync(x => x.Id == rows[2].ContactId)).Status); // ACS Suppressed counts as bounced
        Assert.Equal(ContactStatus.Subscribed, (await db.Contacts.SingleAsync(x => x.Id == rows[0].ContactId)).Status);
        Assert.Equal(6 - 1, await db.EmailEvents.CountAsync());
    }

    [Fact]
    public async Task Third_soft_bounce_in_a_row_is_treated_as_hard()
    {
        var (id, _, rows) = await SentCampaignAsync(1);
        await using (var db = _app.NewDbContext())
            await db.Contacts.Where(c => c.Id == rows[0].ContactId).ExecuteUpdateAsync(u => u.SetProperty(c => c.SoftBounceCount, 2));
        await Delivery("s1", rows[0].AcsMessageId!, "Failed");
        await using var check = _app.NewDbContext();
        var contact = await check.Contacts.SingleAsync(c => c.Id == rows[0].ContactId);
        Assert.Equal(3, contact.SoftBounceCount);
        Assert.Equal(ContactStatus.Bounced, contact.Status);
    }

    [Fact]
    public async Task A_click_counts_once_per_recipient_and_every_click_is_kept()
    {
        var (id, _, rows) = await SentCampaignAsync(2);
        await Click("c1", rows[0].AcsMessageId!, "https://example.com/a");
        await Click("c2", rows[0].AcsMessageId!, "https://example.com/a");
        await Click("c3", rows[0].AcsMessageId!, "https://example.com/b");
        await Click("c4", rows[1].AcsMessageId!, "https://example.com/a");

        // The results page renders the per-link table from these events.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/campaigns/{id}/results");
        request.Headers.Add(CampaignTool.Web.Services.AllowedUsersMiddleware.PrincipalNameHeader, "owner@example.com");
        var page = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("https://example.com/a", html);
        Assert.Contains("https://example.com/b", html);

        await using var db = _app.NewDbContext();
        Assert.Equal(2, (await db.Campaigns.SingleAsync(c => c.Id == id)).Clicked);
        Assert.Equal(3, (await db.CampaignRecipients.SingleAsync(r => r.Id == rows[0].Id)).ClickCount);
        Assert.Equal(4, await db.EmailEvents.CountAsync(e => e.Status == "click"));
    }

    [Fact]
    public async Task Filtered_spam_above_half_a_percent_pauses_the_campaign()
    {
        var (id, _, rows) = await SentCampaignAsync(3, finish: false);
        await Delivery("f1", rows[0].AcsMessageId!, "FilteredSpam");
        await using var db = _app.NewDbContext();
        Assert.Equal(CampaignStatus.Paused, (await db.Campaigns.SingleAsync(c => c.Id == id)).Status);
    }

    [Fact]
    public async Task Unsubscribe_page_and_one_click_unsubscribe_suppress_and_exclude_from_the_next_campaign()
    {
        var (id, listId, rows) = await SentCampaignAsync(3);
        var tokens = _app.Services.GetRequiredService<UnsubscribeTokenService>();
        var token = tokens.Create(rows[0].ContactId, id);

        // Anonymous GET shows a page with one button and no app layout.
        var page = await _http.GetAsync($"/unsubscribe/{token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<button type=\"submit\">Unsubscribe</button>", html);
        Assert.DoesNotContain("mud-layout", html);

        // The button (form POST) unsubscribes; a second POST doesn't double count.
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/unsubscribe/{token}", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/unsubscribe/{token}", new FormUrlEncodedContent([]))).StatusCode);

        // A mail client's one-click POST (RFC 8058) works for another recipient.
        var oneClick = await _http.PostAsync($"/unsubscribe/{tokens.Create(rows[1].ContactId, id)}",
            new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));
        Assert.Equal(HttpStatusCode.OK, oneClick.StatusCode);

        // A tampered token is a plain error.
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"/unsubscribe/{token[..^3]}xyz")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync($"/unsubscribe/{tokens.Create(rows[2].ContactId, id)[..^3]}xyz", new FormUrlEncodedContent([]))).StatusCode);

        await using (var db = _app.NewDbContext())
        {
            Assert.Equal(2, (await db.Campaigns.SingleAsync(c => c.Id == id)).Unsubscribed);
            var gone = await db.Contacts.SingleAsync(c => c.Id == rows[0].ContactId);
            Assert.Equal(ContactStatus.Unsubscribed, gone.Status);
            Assert.True(await db.Suppressions.AnyAsync(s => s.EmailNormalized == gone.EmailNormalized && s.Reason == SuppressionReason.Unsubscribe));
            Assert.Equal(ContactStatus.Subscribed, (await db.Contacts.SingleAsync(c => c.Id == rows[2].ContactId)).Status);
        }

        // The next campaign to the same list skips both.
        using var scope = _app.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CampaignService>();
        var next = await svc.CreateAsync();
        next.Subject = "Again"; next.Html = "<p>Again</p>"; next.ReplyTo = "owner@example.com"; next.ListId = listId;
        await svc.SaveDraftAsync(next);
        Assert.Null(await svc.SendNowAsync(next.Id));
        await using var check = _app.NewDbContext();
        Assert.Equal([rows[2].ContactId], await check.CampaignRecipients.Where(r => r.CampaignId == next.Id).Select(r => r.ContactId).ToListAsync());
    }

    [Fact]
    public async Task Unsubscribe_still_works_after_the_campaign_is_deleted()
    {
        var (id, _, rows) = await SentCampaignAsync(1);
        var token = _app.Services.GetRequiredService<UnsubscribeTokenService>().Create(rows[0].ContactId, id);
        await using (var db = _app.NewDbContext()) await db.Campaigns.Where(c => c.Id == id).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/unsubscribe/{token}", new FormUrlEncodedContent([]))).StatusCode);
        await using var check = _app.NewDbContext();
        Assert.Equal(ContactStatus.Unsubscribed, (await check.Contacts.SingleAsync(c => c.Id == rows[0].ContactId)).Status);
    }

    [Fact]
    public async Task Recipient_export_downloads_every_row()
    {
        var (id, _, _) = await SentCampaignAsync(3);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync($"/campaigns/{id}/recipients.csv")).StatusCode); // behind sign-in
        var request = new HttpRequestMessage(HttpMethod.Get, $"/campaigns/{id}/recipients.csv");
        request.Headers.Add(CampaignTool.Web.Services.AllowedUsersMiddleware.PrincipalNameHeader, "owner@example.com");
        var csv = await (await _http.SendAsync(request)).Content.ReadAsStringAsync();
        var lines = csv.Trim().Split('\n');
        Assert.Equal("email,status,sent_utc,delivered_utc,clicks,attempts,note", lines[0].Trim());
        Assert.Equal(4, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Contains(",Sent,", l));
    }
}
