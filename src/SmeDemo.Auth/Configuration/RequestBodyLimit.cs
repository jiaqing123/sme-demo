using System.Globalization;

namespace SmeDemo.Auth.Configuration;

/// <summary>
/// Resolves the Kestrel request-body ceiling. Auth only accepts small JSON
/// documents (login, verify, resend), so the default matches the 1 MiB limit
/// enforced at the nginx edge; <c>MAX_REQUEST_BODY_BYTES</c> can raise or lower
/// it, and an invalid value fails startup instead of silently falling back.
/// </summary>
public static class RequestBodyLimit
{
    public const long DefaultBytes = 1024 * 1024;

    public static long Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultBytes;
        }

        if (!long.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            || bytes <= 0)
        {
            throw new InvalidOperationException(
                $"MAX_REQUEST_BODY_BYTES must be a positive number of bytes, but was '{configured}'.");
        }

        return bytes;
    }
}
