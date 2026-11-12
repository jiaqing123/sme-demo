namespace SmeDemo.Auth.Configuration;

/// <summary>
/// Configuration-driven options for the issuer. Everything the service needs is
/// resolved once at start-up so misconfiguration fails the container instead of
/// a request.
/// </summary>
public sealed record AuthOptions(
    string DatabaseUrl,
    string OrgId,
    bool DemoMode,
    string DemoUsername,
    string DemoPassword,
    string DemoPhone,
    string DemoOtp,
    bool DemoOtpWasConfigured,
    string JwtIssuer,
    string JwtAudience,
    TimeSpan JwtTtl,
    string CorsOrigin,
    string KeysPath,
    string? SmsProviderUrl,
    AuthRateLimitOptions RateLimits,
    bool TrustForwardedHeaders)
{
    public const string DefaultDemoUsername = "demo";
    public const string DefaultDemoPassword = "Demo_Passw0rd!";
    public const string DefaultDemoPhone = "+61400000000";
    public const string DefaultDemoOtp = "123456";

    public static AuthOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseUrl = Required(configuration, "DATABASE_URL");
        // Fail closed: demo behaviour (fixed OTP, published password) must be an
        // explicit opt-in, never a default that survives into a shared host.
        var demoMode = ReadBool(configuration, "DEMO_MODE", fallback: false);
        var demoUsername = Read(configuration, "DEMO_USERNAME", DefaultDemoUsername);
        var demoPassword = Read(configuration, "DEMO_PASSWORD", DefaultDemoPassword);
        var demoPhone = Read(configuration, "DEMO_PHONE", DefaultDemoPhone);
        var demoOtp = Read(configuration, "DEMO_OTP", DefaultDemoOtp);
        var demoOtpWasConfigured = Optional(configuration, "DEMO_OTP") is not null;
        var smsProviderUrl = Optional(configuration, "SMS_PROVIDER_URL");
        var jwtIssuer = Read(configuration, "JWT_ISSUER", "http://auth:8081").TrimEnd('/');

        if (!Uri.TryCreate(jwtIssuer, UriKind.Absolute, out var issuerUri) ||
            (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("JWT_ISSUER must be an absolute http(s) URL.");
        }

        if (!demoMode && demoOtpWasConfigured)
        {
            throw new InvalidOperationException(
                "DEMO_OTP is set but DEMO_MODE is false. The fixed OTP is refused outside demo mode.");
        }

        if (!demoMode && smsProviderUrl is null)
        {
            throw new InvalidOperationException(
                "SMS_PROVIDER_URL is required when DEMO_MODE is false.");
        }

        if (!demoMode &&
            (string.Equals(demoUsername, DefaultDemoUsername, StringComparison.Ordinal) ||
             string.Equals(demoPassword, DefaultDemoPassword, StringComparison.Ordinal) ||
             string.Equals(demoPhone, DefaultDemoPhone, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "DEMO_USERNAME, DEMO_PASSWORD and DEMO_PHONE must be changed from their demo values " +
                "when DEMO_MODE is false.");
        }

        return new AuthOptions(
            DatabaseUrl: databaseUrl,
            OrgId: Read(configuration, "ORG_ID", "sme"),
            DemoMode: demoMode,
            DemoUsername: demoUsername,
            DemoPassword: demoPassword,
            DemoPhone: demoPhone,
            DemoOtp: demoOtp,
            DemoOtpWasConfigured: demoOtpWasConfigured,
            JwtIssuer: jwtIssuer,
            JwtAudience: Read(configuration, "JWT_AUDIENCE", "sme-demo-api"),
            JwtTtl: DurationParser.Parse(Read(configuration, "JWT_TTL", "30m")),
            CorsOrigin: Read(configuration, "CORS_ORIGIN", "http://localhost:8080").TrimEnd('/'),
            KeysPath: Read(configuration, "KEYS_PATH", "/keys"),
            SmsProviderUrl: smsProviderUrl,
            RateLimits: AuthRateLimitOptions.FromConfiguration(configuration),
            // Compose nginx is the only ingress and sets X-Forwarded-For. Off by
            // default so a direct deployment cannot be spoofed by a client.
            TrustForwardedHeaders: ReadBool(
                configuration,
                "AUTH_TRUST_FORWARDED_HEADERS",
                fallback: false));
    }

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
