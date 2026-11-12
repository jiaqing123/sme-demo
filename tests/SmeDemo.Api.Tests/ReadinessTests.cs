using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Contract tests for the readiness probe. The test host replaces every real
/// probe with a deterministic fake, so these tests never open a SQL connection,
/// call Azurite, or fetch the auth metadata document.
/// </summary>
[TestClass]
public sealed class ReadinessTests
{
    private static readonly string[] ExpectedProbeNames =
        ["database", "blob-storage", "auth-metadata"];

    [TestMethod]
    public async Task Readyz_AllChecksReady_Returns200WithReadyEnvelope()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        // No Authorization header: the probe is anonymous.
        using var response = await client.GetAsync("/readyz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Body: {body}");
        Assert.AreEqual(
            "application/json",
            response.Content.Headers.ContentType?.MediaType,
            "Readiness must be a JSON document.");
        Assert.IsNull(
            response.Headers.RetryAfter,
            "A ready response must not ask the caller to retry.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.AreEqual("ready", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            ExpectedProbeNames,
            checks.Select(check => check.GetProperty("name").GetString()).ToArray(),
            $"Unexpected probe list in: {body}");
        Assert.IsTrue(
            checks.All(check => check.GetProperty("status").GetString() == "ready"),
            $"Every check must be ready. Body: {body}");
    }

    [TestMethod]
    public async Task Readyz_OneDependencyDown_Returns503RetryAfterAndSafeBody()
    {
        using var factory = new ApiTestFactory();
        factory.DatabaseProbe.Ready = false;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/readyz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            response.StatusCode,
            $"A failed dependency must degrade readiness. Body: {body}");
        Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            response.Headers.RetryAfter?.Delta,
            "A 503 must tell the orchestrator how long to wait.");
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.AreEqual("degraded", document.RootElement.GetProperty("status").GetString());

        var statuses = document.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check => check.GetProperty("status").GetString(),
                StringComparer.Ordinal);

        Assert.AreEqual("degraded", statuses["database"]);
        Assert.AreEqual("ready", statuses["blob-storage"]);
        Assert.AreEqual("ready", statuses["auth-metadata"]);

        foreach (var marker in new[]
                 {
                     "Server=",
                     "Password",
                     "UseDevelopmentStorage",
                     "Exception",
                     " at ",
                 })
        {
            Assert.IsFalse(
                body.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"The readiness body must not leak '{marker}'. Body: {body}");
        }
    }

    [TestMethod]
    public async Task Healthz_RemainsLive_WhenReadinessIsDegraded()
    {
        using var factory = new ApiTestFactory();
        factory.SetReadiness(false);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/healthz");
        var liveBody = await live.Content.ReadAsStringAsync();

        Assert.AreEqual(
            HttpStatusCode.OK,
            live.StatusCode,
            $"Liveness must not fail because a dependency is down. Body: {liveBody}");
        using (var document = JsonDocument.Parse(liveBody))
        {
            Assert.AreEqual("ok", document.RootElement.GetProperty("status").GetString());
        }

        using var ready = await client.GetAsync("/readyz");
        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            ready.StatusCode,
            "The same host must still report degraded readiness.");
    }

    [TestMethod]
    public void Readyz_IsAnonymousAndDocumentsBothResponses()
    {
        using var factory = new ApiTestFactory();
        var endpoint = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/readyz");

        Assert.IsNotNull(
            endpoint.Metadata.GetMetadata<IAllowAnonymous>(),
            "/readyz must be explicitly anonymous.");
        Assert.IsNull(
            endpoint.Metadata.GetMetadata<IAuthorizeData>(),
            "/readyz must not inherit the fallback authorization policy.");

        var producedStatuses = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { StatusCodes.Status200OK, StatusCodes.Status503ServiceUnavailable },
            producedStatuses,
            $"Actual produced statuses: {string.Join(", ", producedStatuses)}");
    }

    [TestMethod]
    public async Task Readyz_IsListedInOpenApi_WithBothResponses()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/readyz")
            .GetProperty("get");
        var responses = operation.GetProperty("responses");

        Assert.IsTrue(responses.TryGetProperty("200", out _), "OpenAPI must document /readyz 200.");
        Assert.IsTrue(responses.TryGetProperty("503", out _), "OpenAPI must document /readyz 503.");
        Assert.IsFalse(
            operation.TryGetProperty("security", out _),
            "/readyz must not carry a security requirement in OpenAPI.");
    }
}
