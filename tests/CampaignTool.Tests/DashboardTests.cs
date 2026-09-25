using System.Net;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Components.Shared;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CampaignTool.Tests;

public class DashboardTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero));
    private readonly TestApp _app;

    public DashboardTests()
    {
        _app = new TestApp
        {
            RunWorker = false,
            ConfigureServices = s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(_clock); },
        };
        _app.Settings["Acs:SenderAddress"] = "DoNotReply@test.azurecomm.net";
    }

    public Task InitializeAsync() { _app.CreateClient(); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Totals_daily_series_and_previous_period_come_from_recipients_and_events()
    {
        await using (var db = _app.NewDbContext())
        {
            var list = new ContactList { Name = "L", CreatedAtUtc = Now };
            var campaign = new Campaign { Name = "C", Status = CampaignStatus.Completed, CreatedAtUtc = Now };
            db.Campaigns.Add(campaign);
            db.Lists.Add(list);
            CampaignRecipient Row(int i, DateTime sent, DateTime? delivered)
            {
                var email = $"d{i}@example.com";
                return new CampaignRecipient
                {
                    Campaign = campaign, EmailSnapshot = email, SentAtUtc = sent, DeliveredAtUtc = delivered,
                    Status = delivered is null ? RecipientStatus.Sent : RecipientStatus.Delivered,
                    Contact = new Contact { Email = email, EmailNormalized = email, Status = ContactStatus.Subscribed, StatusChangedAtUtc = Now, Source = "t", CreatedAtUtc = Now, UpdatedAtUtc = Now },
                };
            }
            // Today: 3 sent (one within the last hour), 2 delivered. Ten days ago: 1 sent. 40 days ago (previous period): 2 sent.
            var rows = new[]
            {
                Row(1, Now.AddMinutes(-30), Now.AddMinutes(-29)), Row(2, Now.AddHours(-3), Now.AddHours(-3)), Row(3, Now.AddHours(-4), null),
                Row(4, Now.AddDays(-10), Now.AddDays(-10)), Row(5, Now.AddDays(-40), null), Row(6, Now.AddDays(-40), null),
            };
            db.CampaignRecipients.AddRange(rows);
            await db.SaveChangesAsync();
            // Two clicks by one person and one by another; one bounce.
            db.EmailEvents.AddRange(
                new EmailEvent { EventId = "e1", AcsMessageId = "m", Kind = EmailEventKind.Engagement, Status = "Click", OccurredAtUtc = Now.AddMinutes(-10), CampaignRecipientId = rows[0].Id },
                new EmailEvent { EventId = "e2", AcsMessageId = "m", Kind = EmailEventKind.Engagement, Status = "Click", OccurredAtUtc = Now.AddMinutes(-5), CampaignRecipientId = rows[0].Id },
                new EmailEvent { EventId = "e3", AcsMessageId = "m", Kind = EmailEventKind.Engagement, Status = "Click", OccurredAtUtc = Now.AddDays(-10), CampaignRecipientId = rows[3].Id },
                new EmailEvent { EventId = "e4", AcsMessageId = "m", Kind = EmailEventKind.Delivery, Status = "Bounced", OccurredAtUtc = Now.AddDays(-2), CampaignRecipientId = rows[2].Id });
            await db.SaveChangesAsync();
        }

        using var scope = _app.Services.CreateScope();
        var s = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetAsync();

        Assert.Equal(DashboardService.Days, s.Sent.Daily.Length);
        Assert.Equal(4, s.Sent.Total);
        Assert.Equal(2, s.Sent.PreviousTotal);
        Assert.Equal(3, s.Sent.Daily[^1]);
        Assert.Equal(1, s.Sent.Daily[^11]);
        Assert.Equal(3, s.Delivered.Total);
        Assert.Equal(3, s.Clicks.Total);
        Assert.Equal(2, s.UniqueClickers);
        Assert.Equal(1, s.Bounced.Total);
        Assert.Equal(1, s.SentLastHour);
        Assert.Equal(6, s.Subscribed);
        Assert.NotNull(s.LastReportUtc);
        Assert.Equal("+100%", DashboardService.Change(s.Sent.Total, s.Sent.PreviousTotal));
    }

    [Fact]
    public async Task Dashboard_page_renders_for_the_signed_in_owner()
    {
        using var http = _app.CreateClient();
        http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-NAME", "owner@example.com");
        var response = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Engagement over time", html);
        Assert.Contains("Emails sent", html);
    }

    [Fact]
    public void Chart_points_scale_values_into_the_box()
    {
        Assert.Equal("0,33 120,18 240,3", ChartMath.Polyline([0, 5, 10], 240, 36, 3));
        Assert.Equal("", ChartMath.Polyline([], 240, 36, 3));
        Assert.Equal("0,33 0,33 240,33 240,33", ChartMath.Area([0, 0], 240, 36, 3));
    }
}
