using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Endpoints;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Services;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// The auth service is deliberately outside the API's authorization
/// middleware. Every route it maps must still say so explicitly on the endpoint,
/// so a future fallback policy cannot silently change who can reach login,
/// discovery, JWKS, health or userinfo.
/// </summary>
[TestClass]
public sealed class AuthEndpointMetadataTests
{
    [TestMethod]
    public async Task EveryAuthEndpoint_DeclaresExplicitAllowAnonymousOrAuthorizeMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new AuthOptions(
            DatabaseUrl: "Server=unused;Database=sme_auth;User Id=unused;Password=unused;TrustServerCertificate=True",
            OrgId: "sme",
            DemoMode: true,
            DemoUsername: AuthOptions.DefaultDemoUsername,
            DemoPassword: AuthOptions.DefaultDemoPassword,
            DemoPhone: AuthOptions.DefaultDemoPhone,
            DemoOtp: AuthOptions.DefaultDemoOtp,
            DemoOtpWasConfigured: false,
            JwtIssuer: "http://auth:8081",
            JwtAudience: "sme-demo-api",
            JwtTtl: TimeSpan.FromMinutes(15),
            CorsOrigin: "http://localhost:8080",
            KeysPath: Path.Combine(Path.GetTempPath(), "sme-demo-architecture-tests-keys"),
            SmsProviderUrl: null,
            RateLimits: AuthRateLimitOptions.Default,
            TrustForwardedHeaders: false));

        // The metadata test never invokes a handler, so these registrations only
        // let Minimal API treat the handler parameters as services rather than
        // request bodies. Instantiation is intentionally impossible.
        builder.Services.AddSingleton<SigningKeyStore>(_ =>
            throw new InvalidOperationException("SigningKeyStore must not be resolved by a metadata test."));
        builder.Services.AddScoped<AuthFlowService>();
        builder.Services.AddScoped<UserInfoService>();

        await using var app = builder.Build();
        app.MapAuthEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var routes = endpoints
            .Select(endpoint => NormalizeRoute(endpoint.RoutePattern.RawText))
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        string[] expectedRoutes =
        [
            "/.well-known/jwks.json",
            "/.well-known/openid-configuration",
            "/auth/challenges/{challengeId}/resend",
            "/auth/challenges/{challengeId}/verify",
            "/auth/login",
            "/auth/userinfo",
            "/healthz",
        ];

        CollectionAssert.AreEqual(
            expectedRoutes,
            routes,
            $"AuthEndpoints.cs must keep mapping exactly these routes. Actual: [{string.Join(", ", routes)}].");

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText ?? "<no route pattern>";
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var requiresAuthorization = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;

            Assert.IsTrue(
                allowsAnonymous || requiresAuthorization,
                $"{route} must carry explicit IAllowAnonymous or IAuthorizeData metadata. " +
                "The auth service has no authorization middleware, so an unmarked route " +
                "hides its trust boundary from readers and from a future fallback policy.");
        }
    }

    private static string NormalizeRoute(string? route) =>
        route is null or "/" ? route ?? "<no route pattern>" : route.TrimEnd('/');
}
