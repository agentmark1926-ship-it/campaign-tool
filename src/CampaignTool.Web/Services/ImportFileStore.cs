using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;

namespace CampaignTool.Web.Services;

/// <summary>
/// Uploaded import files: the private blob container in Azure; a temp folder when ConnectionStrings:Storage is empty (local runs, tests).
/// </summary>
public class ImportFileStore(IConfiguration config, IOptions<StorageOptions> storage)
{
    private readonly string? _connection = config.GetConnectionString("Storage");
    private static readonly string LocalRoot = Path.Combine(Path.GetTempPath(), "campaigntool-imports");

    private BlobContainerClient Container => new(_connection, storage.Value.UploadsContainer);

    public async Task<string> SaveAsync(int importId, string fileName, Stream content, CancellationToken ct = default)
    {
        var path = $"{DateTime.UtcNow:yyyy/MM}/{importId}-{Path.GetFileName(fileName)}";
        if (string.IsNullOrWhiteSpace(_connection))
        {
            var file = Path.Combine(LocalRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using var output = File.Create(file);
            await content.CopyToAsync(output, ct);
        }
        else
        {
            await Container.GetBlobClient(path).UploadAsync(content, overwrite: true, ct);
        }
        return path;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connection))
        {
            var file = Path.Combine(LocalRoot, path);
            if (File.Exists(file)) File.Delete(file);
        }
        else
        {
            await Container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(_connection)
            ? File.OpenRead(Path.Combine(LocalRoot, path))
            : await Container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);
}
