using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Errors;

namespace SmeDemo.Infrastructure.Storage;

/// <summary>
/// Card artwork adapter. The browser only ever sees a stream from the API; blob
/// credentials never leave the server (requirements §7.1).
/// </summary>
public sealed class BlobCardArtStore : IBlobStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public BlobCardArtStore(BlobContainerClient container) => _container = container;

    public async Task<BlobContent?> OpenReadAsync(string blobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        await EnsureContainerAsync(cancellationToken);

        var blob = _container.GetBlobClient(blobName);

        // One round trip: a separate ExistsAsync check doubles latency and opens a
        // race where a blob deleted between the two calls becomes a 500.
        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var contentType = string.IsNullOrWhiteSpace(response.Value.Details.ContentType)
                ? "application/octet-stream"
                : response.Value.Details.ContentType;

            return new BlobContent(response.Value.Content, contentType);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException exception)
        {
            throw new StoreUnavailableException("The artwork store is unavailable.", exception);
        }
    }

    public async Task UploadAsync(
        string blobName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        await EnsureContainerAsync(cancellationToken);

        await _container.GetBlobClient(blobName).UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            },
            cancellationToken);
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
