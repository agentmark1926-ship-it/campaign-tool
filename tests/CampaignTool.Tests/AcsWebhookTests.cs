using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Tests;

public class AcsWebhookTests : IAsyncLifetime
{
    private const string Key = "test-webhook-key-0123456789abcdef0123";
    private readonly TestApp _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _app.Settings["Webhooks:AcsSecret"] = Key;
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpResponseMessage> Post(object body, string key = Key) =>
        _client.PostAsJsonAsync($"/webhooks/acs?key={key}", body);

    private static object Delivery(string eventId, string messageId, string status) => new[]
    {
        new
        {
            id = eventId,
            eventType = "Microsoft.Communication.EmailDeliveryReportReceived",
            eventTime = "2026-09-23T12:00:00Z",
            data = new { sender = "DoNotReply@x.azurecomm.net", recipient = "a@example.com", messageId, status, deliveryAttemptTimeStamp = "2026-09-23T12:00:01Z" },
        },
    };

    [Fact]
    public async Task Wrong_or_missing_key_is_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Delivery("e1", "m1", "Delivered"), "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/webhooks/acs", Delivery("e1", "m1", "Delivered"))).StatusCode);
    }

    [Fact]
    public async Task Validation_handshake_is_answered()
    {
        var response = await Post(new[]
        {
            new { id = "v1", eventType = "Microsoft.EventGrid.SubscriptionValidationEvent", eventTime = "2026-09-23T12:00:00Z", data = new { validationCode = "abc-123" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("abc-123", json.GetProperty("validationResponse").GetString());
    }

    [Fact]
    public async Task Delivery_event_for_unknown_message_is_stored_once()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post(Delivery("evt-1", "msg-1", "Delivered"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(Delivery("evt-1", "msg-1", "Delivered"))).StatusCode);

        await using var db = _app.NewDbContext();
        var stored = await db.EmailEvents.SingleAsync();
        Assert.Equal("evt-1", stored.EventId);
        Assert.Equal("msg-1", stored.AcsMessageId);
        Assert.Equal(EmailEventKind.Delivery, stored.Kind);
        Assert.Equal("Delivered", stored.Status);
        Assert.Null(stored.CampaignRecipientId);
        Assert.Equal(new DateTime(2026, 9, 23, 12, 0, 1, DateTimeKind.Utc), stored.OccurredAtUtc);
    }

    [Fact]
    public async Task Click_event_stores_url()
    {
        await Post(new[]
        {
            new
            {
                id = "evt-c",
                eventType = "Microsoft.Communication.EmailEngagementTrackingReportReceived",
                eventTime = "2026-09-23T12:00:00Z",
                data = new { messageId = "msg-2", engagementType = "click", engagementContext = "https://example.com/offer", userAgent = "Mozilla", userActionTimeStamp = "2026-09-23T12:05:00Z" },
            },
        });
        await using var db = _app.NewDbContext();
        var stored = await db.EmailEvents.SingleAsync();
        Assert.Equal(EmailEventKind.Engagement, stored.Kind);
        Assert.Equal("click", stored.Status);
        Assert.Equal("https://example.com/offer", stored.Url);
    }

    [Fact]
    public async Task Other_event_types_are_acknowledged_and_ignored()
    {
        var response = await Post(new[] { new { id = "x", eventType = "Microsoft.Communication.SomethingElse", eventTime = "2026-09-23T12:00:00Z", data = new { } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = _app.NewDbContext();
        Assert.Equal(0, await db.EmailEvents.CountAsync());
    }
}
