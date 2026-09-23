using CampaignTool.Web.Data;

namespace CampaignTool.Web.Endpoints;

public static class HealthEndpoint
{
    public static void MapHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "healthy" })
                : Results.Json(new { status = "database unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable));
}
