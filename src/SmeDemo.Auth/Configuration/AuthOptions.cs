namespace SmeDemo.Auth.Configuration;

/// <summary>
/// Environment-driven configuration for the issuer. Everything the service
/// needs is resolved once at start-up so misconfiguration fails the container
/// instead of a request.
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
    string? SmsProviderUrl)
{
    public const string DefaultDemoUsername = "demo";
    public const string DefaultDemoPassword = "Demo_Passw0rd!";
    public const string DefaultDemoPhone = "+61400000000";
    public const string DefaultDemoOtp = "123456";

    public static AuthOptions FromEnvironment()
    {
        var databaseUrl = Required("DATABASE_URL");
        var demoMode = ReadBool("DEMO_MODE", fallback: true);
        var demoOtp = Read("DEMO_OTP", DefaultDemoOtp);
        var demoOtpWasConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEMO_OTP"));
        var smsProviderUrl = Optional("SMS_PROVIDER_URL");
        var jwtIssuer = Read("JWT_ISSUER", "http://auth:8081").TrimEnd('/');

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

        return new AuthOptions(
            DatabaseUrl: databaseUrl,
            OrgId: Read("ORG_ID", "sme"),
            DemoMode: demoMode,
            DemoUsername: Read("DEMO_USERNAME", DefaultDemoUsername),
            DemoPassword: Read("DEMO_PASSWORD", DefaultDemoPassword),
            DemoPhone: Read("DEMO_PHONE", DefaultDemoPhone),
            DemoOtp: demoOtp,
            DemoOtpWasConfigured: demoOtpWasConfigured,
            JwtIssuer: jwtIssuer,
            JwtAudience: Read("JWT_AUDIENCE", "sme-demo-api"),
            JwtTtl: DurationParser.Parse(Read("JWT_TTL", "30m")),
            CorsOrigin: Read("CORS_ORIGIN", "http://localhost:8080").TrimEnd('/'),
            KeysPath: Read("KEYS_PATH", "/keys"),
            SmsProviderUrl: smsProviderUrl);
    }

    private static string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string Read(string name, string fallback) => Optional(name) ?? fallback;

    private static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var value = Optional(name);
        if (value is null)
        {
            return fallback;
        }

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{name} must be 'true' or 'false'.");
    }
}
