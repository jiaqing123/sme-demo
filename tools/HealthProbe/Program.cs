// Compose healthcheck for the chiseled runtime images, which have no shell and
// no curl. Exits 0 for a 2xx response, 1 for anything else, and 2 when it was
// called without exactly one absolute HTTP(S) URL.
const int Failure = 1;
const int UsageError = 2;

if (args.Length != 1
    || !Uri.TryCreate(args[0], UriKind.Absolute, out var target)
    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
{
    await Console.Error.WriteLineAsync("usage: SmeDemo.HealthProbe <absolute-http(s)-url>");
    return UsageError;
}

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

try
{
    using var response = await client.GetAsync(
        target,
        HttpCompletionOption.ResponseHeadersRead);
    if (response.IsSuccessStatusCode)
    {
        return 0;
    }

    await Console.Error.WriteLineAsync(
        $"[healthprobe] {target} returned {(int)response.StatusCode} ({response.StatusCode})");
    return Failure;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(
        $"[healthprobe] {target} failed: {exception.GetType().Name}: {exception.Message}");
    return Failure;
}
