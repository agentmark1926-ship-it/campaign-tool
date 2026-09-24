using CampaignTool.Web;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.Extensions.Options;

namespace CampaignTool.Tests;

public class CampaignRulesTests
{
    private static readonly (CampaignStatus From, CampaignStatus To)[] Allowed =
    [
        (CampaignStatus.Draft, CampaignStatus.Scheduled), (CampaignStatus.Draft, CampaignStatus.Sending),
        (CampaignStatus.Scheduled, CampaignStatus.Draft), (CampaignStatus.Scheduled, CampaignStatus.Sending),
        (CampaignStatus.Sending, CampaignStatus.Paused), (CampaignStatus.Paused, CampaignStatus.Sending),
        (CampaignStatus.Sending, CampaignStatus.Completed), (CampaignStatus.Sending, CampaignStatus.Cancelled),
        (CampaignStatus.Paused, CampaignStatus.Cancelled),
    ];

    [Fact]
    public void Every_allowed_transition_passes_and_every_other_throws()
    {
        foreach (var from in Enum.GetValues<CampaignStatus>())
        foreach (var to in Enum.GetValues<CampaignStatus>())
        {
            var c = new Campaign { Status = from };
            if (Allowed.Contains((from, to)))
            {
                CampaignRules.Move(c, to);
                Assert.Equal(to, c.Status);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => CampaignRules.Move(c, to));
                Assert.Equal(from, c.Status);
            }
        }
    }

    [Fact]
    public void Retry_schedule_matches_the_backoff_table_and_ends_in_failed()
    {
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(1), CampaignRules.NextAttempt(1, now));
        Assert.Equal(now.AddMinutes(5), CampaignRules.NextAttempt(2, now));
        Assert.Equal(now.AddMinutes(30), CampaignRules.NextAttempt(3, now));
        Assert.Equal(now.AddHours(2), CampaignRules.NextAttempt(4, now));
        Assert.Equal(now.AddHours(6), CampaignRules.NextAttempt(5, now));
        Assert.Null(CampaignRules.NextAttempt(6, now));
    }

    [Theory]
    [InlineData(25, 90)]
    [InlineData(30, 100)]
    [InlineData(5, 12)]
    public void Rate_limiter_never_exceeds_either_window(int perMinute, int perHour)
    {
        var limiter = new RateLimiter();
        var t = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var sends = new List<DateTime>();
        for (var step = 0; step < 3 * 3600 * 4; step++) // every 250 ms for three hours, sending whenever allowed
        {
            var n = limiter.Available(t, perMinute, perHour);
            for (var i = 0; i < n; i++) { limiter.Record(t); sends.Add(t); }
            t = t.AddMilliseconds(250);
        }
        Assert.NotEmpty(sends);
        foreach (var s in sends)
        {
            Assert.True(sends.Count(x => x >= s && x < s.AddMinutes(1)) <= perMinute);
            Assert.True(sends.Count(x => x >= s && x < s.AddHours(1)) <= perHour);
        }
        Assert.Equal(3 * perHour, sends.Count); // and it does use the full allowance
    }

    [Fact]
    public void Rate_limiter_reports_when_the_next_slot_opens()
    {
        var limiter = new RateLimiter();
        var t = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++) limiter.Record(t.AddSeconds(i));
        Assert.Equal(0, limiter.Available(t.AddSeconds(5), 3, 100));
        Assert.Equal(t.AddMinutes(1), limiter.NextAvailable(t.AddSeconds(5), 3, 100));
    }

    private static UnsubscribeTokenService Tokens(string key) => new(
        new OptionsMonitorStub<AuthOptions>(new AuthOptions { UnsubscribeKey = key }),
        new OptionsMonitorStub<AppOptions>(new AppOptions { BaseUrl = "https://app.example.com" }));

    [Fact]
    public void Unsubscribe_token_round_trips_and_rejects_tampering()
    {
        var tokens = Tokens("0123456789abcdef0123456789abcdef");
        var token = tokens.Create(42, 7);
        Assert.True(tokens.TryValidate(token, out var contact, out var campaign));
        Assert.Equal((42, 7), (contact, campaign));
        Assert.Equal($"https://app.example.com/unsubscribe/{token}", tokens.Url(42, 7));

        var forged = Convert.ToBase64String("43:7"u8.ToArray()).TrimEnd('=') + token[token.IndexOf('.')..];
        Assert.False(tokens.TryValidate(forged, out _, out _));
        Assert.False(tokens.TryValidate(token[..^2] + "AA", out _, out _));
        Assert.False(tokens.TryValidate("garbage", out _, out _));
        Assert.False(Tokens("another-key-another-key-another!!").TryValidate(token, out _, out _));
    }

    private sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
