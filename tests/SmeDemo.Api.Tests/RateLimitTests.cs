using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SmeDemo.Api.Tests;

/// <summary>
/// HTTP-level tests for the per-user, per-endpoint API budget
/// (threat-model §5 / §6). A fresh factory with a one-permit window keeps the
/// burst small and proves that endpoints, authentication and the health probes
/// keep their own contracts.
/// </summary>
[TestClass]
public sealed class RateLimitTests
{
    private const string ProblemJson = "application/problem+json";

    [TestMethod]
    public async Task ExhaustingMe_Returns429WithEnvelope_AndAnotherEndpointStillSucceeds()
    {
        using var factory = new ApiTestFactory(
            rateLimitPermit: 1,
            rateLimitWindow: TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var first = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());

        using var rejected = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            rejected.StatusCode,
            await rejected.Content.ReadAsStringAsync());

        var problem = await ReadProblemAsync(rejected);
        Assert.AreEqual(ProblemJson, problem.MediaType, problem.Body);
        Assert.AreEqual(429, problem.JsonStatus, problem.Body);
        Assert.AreEqual("/api/v1/me", problem.Instance, problem.Body);
        Assert.AreEqual("RATE_LIMITED", problem.Code, problem.Body);
        Assert.AreEqual("RATE_LIMITED", problem.Title, problem.Body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.TraceId), problem.Body);
        AssertNumericRetryAfter(rejected);

        // The partition key includes the endpoint name: the same user still has
        // its full budget on a different endpoint in the same window.
        using var cards = await client.GetAsync("/api/v1/me/cards");
        Assert.AreEqual(
            HttpStatusCode.OK,
            cards.StatusCode,
            $"A different endpoint must have its own budget. Body: {await cards.Content.ReadAsStringAsync()}");
    }

    [TestMethod]
    public async Task ExhaustedAuthenticatedBudget_DoesNotReplaceThe401ForAnonymousRequests()
    {
        using var factory = new ApiTestFactory(
            rateLimitPermit: 1,
            rateLimitWindow: TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var first = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var exhausted = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        // Authorization runs before the limiter, so a missing token must still be
        // challenged with the normal 401 envelope rather than a 429.
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            anonymous.StatusCode,
            await anonymous.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "UNAUTHENTICATED",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task HealthAndReadinessProbes_AreNeverRateLimited()
    {
        using var factory = new ApiTestFactory(
            rateLimitPermit: 1,
            rateLimitWindow: TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var first = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var exhausted = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var health = await client.GetAsync("/healthz");
            Assert.AreEqual(
                HttpStatusCode.OK,
                health.StatusCode,
                $"Liveness must not be rate limited. Attempt {attempt}.");

            using var readiness = await client.GetAsync("/readyz");
            Assert.AreEqual(
                HttpStatusCode.OK,
                readiness.StatusCode,
                $"Readiness must not be rate limited. Attempt {attempt}.");
        }
    }

    private static void AssertNumericRetryAfter(HttpResponseMessage response)
    {
        Assert.IsTrue(
            response.Headers.TryGetValues("Retry-After", out var values),
            "A 429 must carry a Retry-After header.");

        var raw = values.Single();
        Assert.IsTrue(
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds),
            $"Retry-After must be numeric seconds, was '{raw}'.");
        Assert.IsTrue(seconds > 0, "Retry-After must be positive.");
    }

    private static async Task<ProblemEnvelope> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var error = root.GetProperty("error");

        return new ProblemEnvelope(
            response.Content.Headers.ContentType?.MediaType,
            root.GetProperty("status").GetInt32(),
            root.GetProperty("title").GetString(),
            root.GetProperty("instance").GetString(),
            root.GetProperty("traceId").GetString(),
            error.GetProperty("code").GetString(),
            body);
    }

    private sealed record ProblemEnvelope(
        string? MediaType,
        int JsonStatus,
        string? Title,
        string? Instance,
        string? TraceId,
        string? Code,
        string Body);
}
