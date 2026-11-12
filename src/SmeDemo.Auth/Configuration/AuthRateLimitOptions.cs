using System.Globalization;

namespace SmeDemo.Auth.Configuration;

/// <summary>
/// Fixed-window budgets for the login, verification and resend endpoints
/// (threat-model §5.1/§5.2). Every value is resolved at start-up so a zero,
/// negative or malformed setting fails the container instead of silently
/// disabling a control. Defaults are deliberately generous for the demo's
/// per-test factory; production adapters tighten them per risk appetite.
/// </summary>
public sealed record AuthRateLimitOptions(
    int LoginIpPermitLimit,
    TimeSpan LoginIpWindow,
    int LoginAccountPermitLimit,
    TimeSpan LoginAccountWindow,
    int VerifyIpPermitLimit,
    TimeSpan VerifyIpWindow,
    int ResendIpPermitLimit,
    TimeSpan ResendIpWindow)
{
    public const int DefaultLoginIpPermitLimit = 20;
    public const int DefaultLoginAccountPermitLimit = 10;
    public const int DefaultVerifyIpPermitLimit = 60;
    public const int DefaultResendIpPermitLimit = 10;

    /// <summary>
    /// The documented defaults for the demo. They are generous enough that the
    /// per-test host fixtures never trip them but still bound a single client.
    /// </summary>
    public static AuthRateLimitOptions Default { get; } = new(
        LoginIpPermitLimit: DefaultLoginIpPermitLimit,
        LoginIpWindow: TimeSpan.FromMinutes(1),
        LoginAccountPermitLimit: DefaultLoginAccountPermitLimit,
        LoginAccountWindow: TimeSpan.FromMinutes(5),
        VerifyIpPermitLimit: DefaultVerifyIpPermitLimit,
        VerifyIpWindow: TimeSpan.FromMinutes(1),
        ResendIpPermitLimit: DefaultResendIpPermitLimit,
        ResendIpWindow: TimeSpan.FromMinutes(1));

    public static AuthRateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new AuthRateLimitOptions(
            LoginIpPermitLimit: ReadPositiveInt(
                configuration,
                "AUTH_RATE_LIMIT_LOGIN_IP",
                DefaultLoginIpPermitLimit),
            LoginIpWindow: ReadPositiveDuration(
                configuration,
                "AUTH_RATE_LIMIT_LOGIN_IP_WINDOW",
                "1m"),
            LoginAccountPermitLimit: ReadPositiveInt(
                configuration,
                "AUTH_RATE_LIMIT_LOGIN_ACCOUNT",
                DefaultLoginAccountPermitLimit),
            LoginAccountWindow: ReadPositiveDuration(
                configuration,
                "AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW",
                "5m"),
            VerifyIpPermitLimit: ReadPositiveInt(
                configuration,
                "AUTH_RATE_LIMIT_VERIFY_IP",
                DefaultVerifyIpPermitLimit),
            VerifyIpWindow: ReadPositiveDuration(
                configuration,
                "AUTH_RATE_LIMIT_VERIFY_IP_WINDOW",
                "1m"),
            ResendIpPermitLimit: ReadPositiveInt(
                configuration,
                "AUTH_RATE_LIMIT_RESEND_IP",
                DefaultResendIpPermitLimit),
            ResendIpWindow: ReadPositiveDuration(
                configuration,
                "AUTH_RATE_LIMIT_RESEND_IP_WINDOW",
                "1m"));
    }

    private static int ReadPositiveInt(IConfiguration configuration, string name, int fallback)
    {
        var value = Optional(configuration, name);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException($"{name} must be a positive integer.");
        }

        return parsed;
    }

    private static TimeSpan ReadPositiveDuration(IConfiguration configuration, string name, string fallback)
    {
        var value = Optional(configuration, name) ?? fallback;

        try
        {
            return DurationParser.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{name} is invalid: {exception.Message}", exception);
        }
    }

    private static string? Optional(IConfiguration configuration, string name)
    {
        var value = configuration[name];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
