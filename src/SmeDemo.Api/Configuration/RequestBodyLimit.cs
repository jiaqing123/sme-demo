using System.Globalization;

namespace SmeDemo.Api.Configuration;

/// <summary>
/// Resolves the Kestrel request-body ceiling. The demo has no upload endpoint,
/// so the default matches the 1 MiB limit enforced at the nginx edge: a caller
/// that reaches the published API port directly cannot push a larger body than
/// the browser-facing path would allow. Set <c>MAX_REQUEST_BODY_BYTES</c> to a
/// positive byte count to raise or lower it; an invalid value fails startup
/// instead of silently falling back.
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
