using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampaignTool.Web.Services;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Endpoints;

/// <summary>Event Grid (EventGridSchema) → POST /webhooks/acs?key=&lt;Webhooks:AcsSecret&gt;.</summary>
public static class AcsWebhookEndpoint
{
    private const string ValidationEvent = "Microsoft.EventGrid.SubscriptionValidationEvent";

    public static void MapAcsWebhookEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/webhooks/acs", async (HttpRequest request, IOptionsMonitor<WebhookOptions> options, EventProcessor processor, CancellationToken ct) =>
        {
            if (!KeyMatches(request.Query["key"], options.CurrentValue.AcsSecret))
                return Results.Text("Invalid key.", statusCode: StatusCodes.Status401Unauthorized);

            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
            catch (JsonException) { return BadRequest(); }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return BadRequest();
                foreach (var ev in doc.RootElement.EnumerateArray())
                {
                    if (ev.TryGetProperty("eventType", out var type) && type.GetString() == ValidationEvent)
                        return Results.Ok(new { validationResponse = ev.GetProperty("data").GetProperty("validationCode").GetString() });
                    await processor.StoreAsync(ev, ct);
                }
            }
            return Results.Ok();
        }).DisableAntiforgery();

    // Error responses carry a body so the status-code page middleware leaves them alone.
    private static IResult BadRequest() => Results.Text("Expected an Event Grid event array.", statusCode: StatusCodes.Status400BadRequest);

    private static bool KeyMatches(string? presented, string expected) =>
        !string.IsNullOrEmpty(expected) && presented is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}
