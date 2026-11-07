namespace SmeDemo.Api.Configuration;

/// <summary>
/// Environment-driven configuration for the resource API. Resolved once at
/// start-up so a bad value fails the container instead of a request.
/// </summary>
public sealed record ApiOptions(
    string DatabaseUrl,
    string OrgId,
    string CorsOrigin,
    string AuthMetadataUrl,
    string JwtIssuer,
    string JwtAudience,
    bool RequireHttpsMetadata,
    string PublicBaseUrl,
    bool SeedEnabled,
    string StorageConnectionString,
    string TransactionsTable,
    string CardArtContainer)
{
    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var jwtIssuer = Read(configuration, "JWT_ISSUER", "http://auth:8081").TrimEnd('/');
        var metadataUrl = Read(
            configuration,
            "AUTH_METADATA_URL",
            "http://auth:8081/.well-known/openid-configuration");
        var publicBaseUrl = Read(configuration, "PUBLIC_BASE_URL", "http://localhost:8082").TrimEnd('/');

        if (!IsAbsoluteHttpUrl(jwtIssuer))
        {
            throw new InvalidOperationException("JWT_ISSUER must be an absolute http(s) URL.");
        }

        if (!IsAbsoluteHttpUrl(metadataUrl))
        {
            throw new InvalidOperationException("AUTH_METADATA_URL must be an absolute http(s) URL.");
        }

        if (!IsAbsoluteHttpUrl(publicBaseUrl))
        {
            throw new InvalidOperationException("PUBLIC_BASE_URL must be an absolute http(s) URL.");
        }

        return new ApiOptions(
            DatabaseUrl: Required(configuration, "DATABASE_URL"),
            OrgId: Read(configuration, "ORG_ID", "sme"),
            CorsOrigin: Read(configuration, "CORS_ORIGIN", "http://localhost:8080").TrimEnd('/'),
            AuthMetadataUrl: metadataUrl,
            JwtIssuer: jwtIssuer,
            JwtAudience: Read(configuration, "JWT_AUDIENCE", "sme-demo-api"),
            // Secure by default; compose.yaml is the only place that turns this
            // off, with the reason documented there.
            RequireHttpsMetadata: ReadBool(configuration, "AUTH_REQUIRE_HTTPS_METADATA", fallback: true),
            PublicBaseUrl: publicBaseUrl,
            SeedEnabled: ReadBool(configuration, "SEED_ENABLED", fallback: true),
            StorageConnectionString: Read(
                configuration,
                "AZURE_STORAGE_CONNECTION_STRING",
                "UseDevelopmentStorage=true"),
            TransactionsTable: Read(configuration, "TRANSACTIONS_TABLE", "transactions"),
            CardArtContainer: Read(configuration, "CARD_ART_CONTAINER", "card-art"));
    }

    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Required(IConfiguration configuration, string name) =>
        Optional(configuration, name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string Read(IConfiguration configuration, string name, string fallback) =>
        Optional(configuration, name) ?? fallback;

    private static string? Optional(IConfiguration configuration, string name)
    {
        var value = configuration[name];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBool(IConfiguration configuration, string name, bool fallback)
    {
        var value = Optional(configuration, name);
        if (value is null)
        {
            return fallback;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be 'true' or 'false'.");
    }
}
