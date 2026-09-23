using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// Campaign images in the public blob container, cached for a year. Locally (no storage connection)
/// they go to a temp folder and are served from /assets/.
/// </summary>
public class AssetStore(IConfiguration config, IOptions<StorageOptions> storage)
{
    public const long MaxImageBytes = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private readonly string? _connection = config.GetConnectionString("Storage");
    public static readonly string LocalRoot = Path.Combine(Path.GetTempPath(), "campaigntool-assets");

    public static string? ContentType(string fileName) => Types.GetValueOrDefault(Path.GetExtension(fileName));

    /// <summary>Returns the public URL.</summary>
    public async Task<string> SaveImageAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        var type = ContentType(fileName) ?? throw new ArgumentException("Upload a PNG, JPG, GIF or WebP image.");
        var name = $"images/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}";
        if (string.IsNullOrWhiteSpace(_connection))
        {
            var file = Path.Combine(LocalRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using var output = File.Create(file);
            await content.CopyToAsync(output, ct);
            return "/assets/" + name;
        }
        var blob = new BlobContainerClient(_connection, storage.Value.PublicContainer).GetBlobClient(name);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = type, CacheControl = "public, max-age=31536000, immutable" },
        }, ct);
        return blob.Uri.ToString();
    }
}
