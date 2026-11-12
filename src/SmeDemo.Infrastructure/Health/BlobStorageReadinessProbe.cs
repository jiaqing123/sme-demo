using Azure.Storage.Blobs;
using SmeDemo.Application.Abstractions;

namespace SmeDemo.Infrastructure.Health;

/// <summary>
/// Proves the Blob Storage account behind card artwork is reachable. It reads
/// account service properties rather than a single container so readiness does
/// not depend on whether the seeder has created the artwork container yet.
/// </summary>
public sealed class BlobStorageReadinessProbe(BlobServiceClient client) : IReadinessProbe
{
    public string Name => "blob-storage";

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        _ = await client.GetPropertiesAsync(cancellationToken);
        return true;
    }
}
