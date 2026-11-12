namespace SmeDemo.Auth.RateLimiting;

/// <summary>
/// Named limiter policies registered by the host. Keeping the names in one place
/// prevents an endpoint and its policy registration from drifting apart.
/// </summary>
public static class AuthRateLimitPolicies
{
    public const string LoginIp = "auth-login-ip";
    public const string VerifyIp = "auth-challenge-verify-ip";
    public const string ResendIp = "auth-challenge-resend-ip";

    /// <summary>
    /// The per-account login budget is checked inside
    /// <see cref="Services.AuthFlowService"/> after request-shape validation, so
    /// it has a name but is not attached to the endpoint as middleware metadata.
    /// </summary>
    public const string LoginAccount = "auth-login-account";
}
