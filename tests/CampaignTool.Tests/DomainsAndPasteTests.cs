using System.Net;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CampaignTool.Tests;

public class DomainsAndPasteTests : IAsyncLifetime
{
    // 15:00 UTC = 10:00 in Chicago, so "today" has 14 hours left.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero));
    private readonly ScriptedSender _sender = new();
    private readonly TestApp _app;

    public DomainsAndPasteTests()
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
        _app.Settings["App:TimeZone"] = "America/Chicago";
        _app.Settings["Auth:UnsubscribeKey"] = "test-unsubscribe-key-0123456789abcdef";
        _app.Settings["Acs:SenderAddress"] = "DoNotReply@news.example.com";
        _app.Settings["Acs:SenderAddresses"] = "DoNotReply@news.example.com,DoNotReply@test.azurecomm.net";
    }

    public Task InitializeAsync() { _app.CreateClient(); return Task.CompletedTask; }
    public async Task DisposeAsync() => await _app.DisposeAsync();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _app.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    [Fact]
    public void Links_are_normalized_escaped_and_inserted_at_the_cursor()
    {
        Assert.Equal("https://scoutsearchgroup.com/", LinkBuilder.Normalize("scoutsearchgroup.com"));
        Assert.Equal("https://x.com/a?b=1", LinkBuilder.Normalize(" https://x.com/a?b=1 "));
        Assert.Equal("mailto:jane@x.com", LinkBuilder.Normalize("mailto:jane@x.com"));
        Assert.Null(LinkBuilder.Normalize("javascript:alert(1)"));
        Assert.Null(LinkBuilder.Normalize("not a link"));

        Assert.Equal("<a href=\"https://x.com/?a=1&amp;b=2\" style=\"color:#0F766E;text-decoration:underline\">Tom &amp; Co</a>",
            LinkBuilder.Link("Tom & Co", "https://x.com/?a=1&b=2", html: true));
        Assert.Equal("Our site: https://x.com/", LinkBuilder.Link("Our site", "https://x.com/", html: false));
        Assert.Contains(">https://x.com/</a>", LinkBuilder.Link("", "https://x.com/", html: true));
        Assert.Contains("<table role=\"presentation\"", LinkBuilder.Button("Reserve", "https://x.com/"));

        Assert.Equal("Visit [L] today", LinkBuilder.Insert("Visit us today", "[L]", 6, 8));
        Assert.Equal("Hello[L]", LinkBuilder.Insert("Hello", "[L]", -1, -1));
    }

    [Fact]
    public void Pasted_text_yields_unique_valid_addresses_and_reports_bad_ones()
    {
        var (valid, invalid) = ContactService.ParsePasted("Jane <Jane@Firm.com>, bob@fund.com;\nbob@fund.com\tsam@\n\"Ann Lee\" ann@x.co mailto:zed@y.org");
        Assert.Equal(["jane@firm.com", "bob@fund.com", "ann@x.co", "zed@y.org"], valid);
        Assert.Equal(["sam@"], invalid);
    }

    [Fact]
    public async Task Pasting_creates_a_list_keeps_unsubscribed_people_excluded_and_the_campaign_sends_only_to_the_rest()
    {
        await using (var db = _app.NewDbContext())
        {
            db.Contacts.Add(new Contact { Email = "gone@x.com", EmailNormalized = "gone@x.com", Status = ContactStatus.Unsubscribed, StatusChangedAtUtc = Now, Source = "t", CreatedAtUtc = Now, UpdatedAtUtc = Now });
            await db.SaveChangesAsync();
        }

        var result = await With(sp => sp.GetRequiredService<ContactService>().AddPastedAsync("a@x.com, b@x.com\ngone@x.com, nope@", "Brokers"));
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.AlreadyContacts);
        Assert.Equal(1, result.NotSubscribed);
        Assert.Equal(["nope@"], result.Invalid);

        var again = await With(sp => sp.GetRequiredService<ContactService>().AddPastedAsync("a@x.com", "Brokers"));
        Assert.Equal("Brokers (2)", again.ListName);
        Assert.Equal(0, again.Added);

        await using var check = _app.NewDbContext();
        Assert.Equal(ContactStatus.Unsubscribed, (await check.Contacts.SingleAsync(c => c.EmailNormalized == "gone@x.com")).Status);
        Assert.Equal("pasted", (await check.Contacts.SingleAsync(c => c.EmailNormalized == "a@x.com")).Source);
        var sendable = await With(sp => sp.GetRequiredService<CampaignService>().Sendable(result.ListId, []).Select(c => c.EmailNormalized).OrderBy(e => e).ToListAsync());
        Assert.Equal(["a@x.com", "b@x.com"], sendable);
    }

    [Fact]
    public async Task A_domains_daily_limit_holds_its_campaign_until_midnight_and_the_domain_page_shows_the_counts()
    {
        Assert.Null(await With(sp => sp.GetRequiredService<DomainService>().SetDailyLimitAsync("news.example.com", 5)));

        var ids = await With(async sp =>
        {
            var contacts = sp.GetRequiredService<ContactService>();
            var pasted = await contacts.AddPastedAsync(string.Join(",", Enumerable.Range(0, 8).Select(i => $"cap{i}@example.com")), "Cap");
            var svc = sp.GetRequiredService<CampaignService>();
            var c = await svc.CreateAsync();
            c.Subject = "Hi";
            c.Html = "<p>Hi</p>";
            c.ReplyTo = "owner@example.com";
            c.ListId = pasted.ListId;
            Assert.Null(await svc.SaveDraftAsync(c));
            Assert.Null(await svc.SendNowAsync(c.Id));
            return c.Id;
        });

        DateTime? next = Now;
        for (var i = 0; i < 20 && next <= Now; i++)
            next = await With(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());

        Assert.Equal(5, _sender.Calls.Count(e => e.To.StartsWith("cap")));
        // Midnight in Chicago (CDT, UTC-5) is 05:00 UTC the next day.
        Assert.Equal(new DateTime(2026, 9, 26, 5, 0, 0), next);

        var domains = await With(sp => sp.GetRequiredService<DomainService>().ListAsync());
        var news = domains.Single(d => d.Domain == "news.example.com");
        Assert.Equal(5, news.DailyLimit);
        Assert.Equal(5, news.SentToday);
        Assert.Equal(5, news.Last30Days.Sent);
        Assert.Equal(DomainService.Health.New, news.Health);
        Assert.True(domains.Single(d => d.Domain == "test.azurecomm.net").IsTestDomain);

        // Next day the rest goes out.
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 26, 5, 0, 1, TimeSpan.Zero));
        for (var i = 0; i < 20; i++) await With(sp => sp.GetRequiredService<CampaignSender>().SendBatchAsync());
        Assert.Equal(8, _sender.Calls.Count(e => e.To.StartsWith("cap")));
        await using var db = _app.NewDbContext();
        Assert.Equal(CampaignStatus.Completed, (await db.Campaigns.SingleAsync(c => c.Id == ids)).Status);
    }

    [Fact]
    public void Health_follows_bounce_and_spam_thresholds()
    {
        Assert.Equal(DomainService.Health.New, DomainService.Assess(new(10, 10, 0, 0, 0, 0, 0)).Health);
        Assert.Equal(DomainService.Health.Healthy, DomainService.Assess(new(1000, 990, 5, 0, 0, 2, 40)).Health);
        Assert.Equal(DomainService.Health.Watch, DomainService.Assess(new(1000, 980, 12, 0, 0, 2, 40)).Health);
        Assert.Equal(DomainService.Health.AtRisk, DomainService.Assess(new(1000, 970, 25, 0, 0, 2, 40)).Health);
        Assert.Equal(DomainService.Health.AtRisk, DomainService.Assess(new(1000, 990, 0, 0, 4, 0, 40)).Health);
        Assert.Equal(100, DomainService.Assess(new(1000, 1000, 0, 0, 0, 0, 0)).Score);
    }

    [Fact]
    public async Task Domains_page_renders()
    {
        using var http = _app.CreateClient();
        http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-NAME", "owner@example.com");
        var response = await http.GetAsync("/domains");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("news.example.com", html);
        Assert.Contains("Google Postmaster Tools", html);
    }
}
