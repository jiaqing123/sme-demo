using System.Net;

namespace SmeDemo.Api.Tests;

/// <summary>
/// The readiness cache exists so repeated probes do not multiply downstream
/// calls. These tests drive the real /readyz endpoint with fake probes and a
/// manual clock.
/// </summary>
[TestClass]
public sealed class ReadinessCacheTests
{
    [TestMethod]
    public async Task Readyz_RepeatedCallsWithinCacheWindow_ProbeOnce()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/readyz")).EnsureSuccessStatusCode();
        (await client.GetAsync("/readyz")).EnsureSuccessStatusCode();

        Assert.AreEqual(
            1,
            factory.DatabaseProbe.Calls,
            "Two probes inside the cache window must share one snapshot.");
    }

    [TestMethod]
    public async Task Readyz_AfterCacheWindow_RefreshesAndSeesRecovery()
    {
        using var factory = new ApiTestFactory();
        factory.SetReadiness(false);
        using var client = factory.CreateClient();

        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/readyz")).StatusCode);

        // The dependency recovers, but the cached degraded snapshot is still
        // valid: a probe storm must not reach the dependency for every request.
        factory.SetReadiness(true);
        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/readyz")).StatusCode);

        factory.Clock.Advance(ReadinessCacheWindow + TimeSpan.FromSeconds(1));

        Assert.AreEqual(
            HttpStatusCode.OK,
            (await client.GetAsync("/readyz")).StatusCode,
            "Once the snapshot expires, the recovered dependency must be reported.");

        Assert.AreEqual(
            2,
            factory.DatabaseProbe.Calls,
            "A refresh after expiry must probe every dependency exactly once more.");
    }

    [TestMethod]
    public async Task Readyz_StoresOnlyTheSnapshot_NotProbeDetail()
    {
        using var factory = new ApiTestFactory();
        factory.DatabaseProbe.Ready = false;
        using var client = factory.CreateClient();

        using var firstResponse = await client.GetAsync("/readyz");
        using var secondResponse = await client.GetAsync("/readyz");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, secondResponse.StatusCode);

        var first = await firstResponse.Content.ReadAsStringAsync();
        var second = await secondResponse.Content.ReadAsStringAsync();

        // The cached response is the same stable JSON contract, not a replay of
        // a probe's internal state.
        Assert.AreEqual(first, second);
        StringAssert.Contains(first, "\"status\":\"degraded\"");
    }

    private static readonly TimeSpan ReadinessCacheWindow = TimeSpan.FromSeconds(2);
}
