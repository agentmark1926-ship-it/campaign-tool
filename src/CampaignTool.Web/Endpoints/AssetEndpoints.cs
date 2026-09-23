using CampaignTool.Web.Services;

namespace CampaignTool.Web.Endpoints;

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
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
