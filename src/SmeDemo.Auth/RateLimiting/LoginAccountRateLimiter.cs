using System.Threading.RateLimiting;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.RateLimiting;

/// <summary>
/// Per-account fixed-window budget for <c>POST /auth/login</c>. It is a dedicated
/// singleton rather than endpoint middleware because the partition key (the
/// normalized username) only exists after the request body has been validated;
/// checking it any earlier would let malformed requests drain an account's
/// budget and would expose a different response before the body is parsed.
/// </summary>
public sealed class LoginAccountRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _window;

    public LoginAccountRateLimiter(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limits = options.RateLimits;
        _window = limits.LoginAccountWindow;

        _limiter = PartitionedRateLimiter.Create<string, string>(
            username => RateLimitPartition.GetFixedWindowLimiter(
                username,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.LoginAccountPermitLimit,
                    Window = _window,
                    // Reject immediately: a queued login attempt would hold a
                    // request thread only to be rejected after the window resets.
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                }),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Attempts one login for the normalized username. On rejection
    /// <paramref name="retryAfter"/> is the lease's own retry hint when the
    /// limiter supplies one, otherwise the configured window.
    /// </summary>
    public bool TryAcquire(string normalizedUsername, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedUsername);

        using var lease = _limiter.AttemptAcquire(normalizedUsername, 1);
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var fromLease)
            ? fromLease
            : _window;
        return false;
    }

    public void Dispose() => _limiter.Dispose();
}
