using System.Text.Json;
using SmeDemo.Api.Configuration;
using SmeDemo.Application.Abstractions;

namespace SmeDemo.Api.Readiness;

/// <summary>
/// Proves the auth service still publishes usable OpenID metadata. The API
/// validates tokens against that document, so auth being down is a readiness
/// failure even though it is not a local store failure. A JSON object is not
/// enough: the document must name the issuer the API validates and an absolute
/// <c>jwks_uri</c> the token handler can fetch.
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

        return IsUsableMetadata(document.RootElement, options);
    }

    private static bool IsUsableMetadata(JsonElement root, ApiOptions options)
    {
        // A successful status with an HTML error page, a partial document, or a
        // document for another issuer is still a broken metadata endpoint.
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!root.TryGetProperty("issuer", out var issuer)
            || issuer.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(issuer.GetString()))
        {
            return false;
        }

        if (!string.Equals(
                issuer.GetString()!.TrimEnd('/'),
                options.JwtIssuer.TrimEnd('/'),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("jwks_uri", out var jwksUri)
            || jwksUri.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(jwksUri.GetString(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Metadata fetched over HTTPS must not point the key download at plain
        // HTTP; JwtBearer would refuse it and every request would fail.
        return !options.RequireHttpsMetadata || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool IsJson(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}
