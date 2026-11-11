using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// The resource API's published contract: every <c>/api/v1</c> endpoint must be
/// protected by the <c>ApiUser</c> policy and must expose a stable name and at
/// least one documented response type. These are the three metadata contracts
/// generated clients and the OpenAPI document rely on.
/// </summary>
[TestClass]
public sealed class ApiEndpointContractTests
{
    [TestMethod]
    public void EveryApiV1Endpoint_RequiresApiUser_AndDeclaresNameAndResponseType()
    {
        using var factory = new ArchitectureApiFactory();
        var endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsApiV1Route(endpoint.RoutePattern.RawText))
            .ToArray();

        Assert.IsTrue(
            endpoints.Length > 0,
            "No /api/v1 endpoints were mapped from the real API host; the contract check would be vacuous.");

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText ?? "<no route pattern>";

            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.IsTrue(
                authorization.Any(data => string.Equals(data.Policy, "ApiUser", StringComparison.Ordinal)),
                $"{route} must require the ApiUser authorization policy. " +
                $"Actual policies: [{string.Join(", ", authorization.Select(data => data.Policy ?? "<none>"))}].");

            var endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>();
            Assert.IsNotNull(
                endpointName,
                $"{route} must declare an endpoint name via .WithName(...); " +
                "clients and the OpenAPI operationId depend on it.");

            var produces = endpoint.Metadata.GetMetadata<IProducesResponseTypeMetadata>();
            Assert.IsNotNull(
                produces,
                $"{route} must declare at least one response type via .Produces(...) or .ProducesProblem(...).");
        }
    }

    private static bool IsApiV1Route(string? route) =>
        route is not null &&
        (string.Equals(route, "/api/v1", StringComparison.Ordinal) ||
         route.StartsWith("/api/v1/", StringComparison.Ordinal));
}
