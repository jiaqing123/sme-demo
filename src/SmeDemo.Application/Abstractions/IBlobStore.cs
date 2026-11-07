namespace SmeDemo.Application.Abstractions;

public sealed record BlobContent(Stream Content, string ContentType);

/// <summary>
/// Card artwork blobs. The API reads streams; the seeder writes the static
/// artwork during start-up.
/// </summary>
public interface IBlobStore
{
    Task<BlobContent?> OpenReadAsync(string blobName, CancellationToken cancellationToken);

    Task UploadAsync(
        string blobName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);
}
