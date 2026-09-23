using CampaignTool.Web.Services;

namespace CampaignTool.Web.Endpoints;

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        // Image upload from the editor (signed-in owner only; the sign-in check covers this path).
        app.MapPost("/assets/images", async (IFormFile file, AssetStore assets, CancellationToken ct) =>
        {
            if (file.Length is 0 or > AssetStore.MaxImageBytes)
                return Results.Text("Images must be between 1 byte and 5 MB.", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                await using var stream = file.OpenReadStream();
                return Results.Ok(new { url = await assets.SaveImageAsync(file.FileName, stream, ct) });
            }
            catch (ArgumentException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).DisableAntiforgery();

        // Local runs only: in Azure images are served straight from the public blob container.
        app.MapGet("/assets/{**path}", (string path) =>
        {
            var full = Path.GetFullPath(Path.Combine(AssetStore.LocalRoot, path));
            return full.StartsWith(AssetStore.LocalRoot) && File.Exists(full) && AssetStore.ContentType(full) is { } type
                ? Results.File(full, type)
                : Results.NotFound();
        });
    }
}
