using System.Text.Json;
using SmeDemo.Api.Configuration;
using SmeDemo.Application.Abstractions;

namespace SmeDemo.Api.Readiness;

/// <summary>
/// Proves the auth service still publishes usable OpenID metadata. The API
/// validates tokens against that document, so auth being down is a readiness
/// failure even though it is not a local store failure.
/// </summary>
internal sealed class AuthMetadataReadinessProbe(HttpClient client, ApiOptions options) : IReadinessProbe
{
    public string Name => "auth-metadata";

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            options.AuthMetadataUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode ||
            !IsJson(response.Content.Headers.ContentType?.MediaType))
        {
            return false;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        // A successful status with an HTML error page is still a broken
        // metadata endpoint; require a JSON object (the discovery document).
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }

    private static bool IsJson(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
