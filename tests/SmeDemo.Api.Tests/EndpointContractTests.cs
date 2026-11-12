using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Api.Http;

namespace SmeDemo.Api.Tests;

[TestClass]
public sealed class EndpointContractTests
{
    private static readonly string[] ExpectedApiRoutes =
    [
        "/api/v1/cards/{cardId:guid}/art",
        "/api/v1/me",
        "/api/v1/me/cards",
        "/api/v1/me/cards/{cardId:guid}/transactions",
    ];

    [TestMethod]
    public void ResourceEndpoints_RequireAuthorizationAndDeclareResponses()
    {
        using var factory = new ApiTestFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        var apiEndpoints = endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedApiRoutes.OrderBy(route => route, StringComparer.Ordinal).ToArray(),
            apiEndpoints
                .Select(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText!))
                .OrderBy(route => route, StringComparer.Ordinal)
                .ToArray(),
            $"Actual API routes: {string.Join(" | ", apiEndpoints.Select(endpoint => endpoint.RoutePattern.RawText))}");

        foreach (var endpoint in apiEndpoints)
        {
            var authorizeData = endpoint.Metadata.GetMetadata<IAuthorizeData>();
            Assert.IsNotNull(
                authorizeData,
                $"{endpoint.RoutePattern.RawText} must require authorization.");
            Assert.AreEqual("ApiUser", authorizeData.Policy, $"{endpoint.RoutePattern.RawText} must use the ApiUser policy.");
            Assert.IsNotNull(
                endpoint.Metadata.GetMetadata<IProducesResponseTypeMetadata>(),
                $"{endpoint.RoutePattern.RawText} must declare at least one response type.");
            Assert.IsNotNull(
                endpoint.Metadata.GetMetadata<IEndpointNameMetadata>(),
                $"{endpoint.RoutePattern.RawText} must have an endpoint name.");
        }

        var me = apiEndpoints.Single(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText!) == "/api/v1/me");
        Assert.AreEqual(
            "no-store",
            me.Metadata.GetMetadata<CacheControlMetadata>()?.Value);

        var art = apiEndpoints.Single(
            endpoint => NormalizeRoute(endpoint.RoutePattern.RawText!) == "/api/v1/cards/{cardId:guid}/art");
        Assert.AreEqual(
            "private, max-age=3600",
            art.Metadata.GetMetadata<CacheControlMetadata>()?.Value);

        var health = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/healthz");
        Assert.IsNotNull(health.Metadata.GetMetadata<IAllowAnonymous>(), "/healthz must be anonymous.");
        Assert.IsNull(health.Metadata.GetMetadata<IAuthorizeData>(), "/healthz must not require authorization.");

        var readiness = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/readyz");
        Assert.IsNotNull(readiness.Metadata.GetMetadata<IAllowAnonymous>(), "/readyz must be anonymous.");
        Assert.IsNull(readiness.Metadata.GetMetadata<IAuthorizeData>(), "/readyz must not require authorization.");
        CollectionAssert.AreEquivalent(
            new[] { StatusCodes.Status200OK, StatusCodes.Status503ServiceUnavailable },
            readiness.Metadata.OfType<IProducesResponseTypeMetadata>()
                .Select(metadata => metadata.StatusCode)
                .ToArray(),
            "/readyz must document both the ready and degraded responses.");
    }

    [TestMethod]
    public async Task CacheControl_IsAppliedToMeResponses_ButNotArtErrors()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        StringAssert.Contains(
            anonymous.Headers.CacheControl?.ToString() ?? string.Empty,
            "no-store");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        var me = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        StringAssert.Contains(
            me.Headers.CacheControl?.ToString() ?? string.Empty,
            "no-store");

        var art = await client.GetAsync($"/api/v1/cards/{Guid.NewGuid()}/art");
        Assert.AreEqual(HttpStatusCode.NotFound, art.StatusCode);

        // Artwork errors must not inherit the success-path private cache policy.
        // The framework exception handler still stamps its own no-cache header,
        // which predates this refactor and is asserted only for the absence of
        // the art policy here.
        var artCacheControl = art.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.IsFalse(artCacheControl.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(artCacheControl.Contains("max-age", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRoute(string route) =>
        route.Length > 1 ? route.TrimEnd('/') : route;

    [TestMethod]
    public async Task OpenApiDocument_IsAnonymousAndListsApiRoutes()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var json = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        foreach (var route in new[]
        {
            "/api/v1/me",
            "/api/v1/me/cards",
            "/api/v1/me/cards/{cardId}/transactions",
            "/api/v1/cards/{cardId}/art",
        })
        {
            Assert.IsTrue(paths.TryGetProperty(route, out _), $"OpenAPI paths must contain {route}.");
        }
    }
}
