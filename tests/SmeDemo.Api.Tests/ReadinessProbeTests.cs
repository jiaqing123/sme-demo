using System.Net;
using System.Text;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Readiness;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Exercises the real <see cref="AuthMetadataReadinessProbe"/> against a fake
/// HTTP handler. The endpoint contract tests replace every probe with a fake,
/// so these tests keep the production document validation covered without a
/// network call or the API test host.
/// </summary>
[TestClass]
public sealed class ReadinessProbeTests
{
    [TestMethod]
    public async Task Check_AcceptsValidMetadata()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            CreateOptions());

        Assert.IsTrue(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsMetadataForAnotherIssuer()
    {
        var probe = CreateProbe(
            """{"issuer":"http://evil:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            CreateOptions());

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsRelativeJwksUri()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"/.well-known/jwks.json"}""",
            CreateOptions());

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsHtmlContentType()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            CreateOptions(),
            mediaType: "text/html");

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsPlainHttpJwksWhenHttpsIsRequired()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            CreateOptions(requireHttpsMetadata: true));

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_AcceptsJsonCompatibleMediaType()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            CreateOptions(),
            mediaType: "application/vnd.auth.metadata+json");

        Assert.IsTrue(await probe.CheckAsync(CancellationToken.None));
    }

    private static ApiOptions CreateOptions(bool requireHttpsMetadata = false) => new(
        DatabaseUrl: "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
        OrgId: "sme",
        CorsOrigin: "http://localhost:8080",
        AuthMetadataUrl: "http://auth:8081/.well-known/openid-configuration",
        JwtIssuer: "http://auth:8081",
        JwtAudience: "sme-demo-api",
        RequireHttpsMetadata: requireHttpsMetadata,
        PublicBaseUrl: "http://localhost:8082",
        SeedEnabled: false,
        StorageConnectionString: "UseDevelopmentStorage=true",
        TransactionsTable: "transactions",
        CardArtContainer: "card-art",
        IssuerBaseUrl: "http://issuer-mock:8084",
        IssuerApiKey: "test-issuer-key",
        IssuerSyncInterval: TimeSpan.FromMinutes(5),
        IssuerSyncEnabled: false,
        RateLimitPermit: 120,
        RateLimitWindow: TimeSpan.FromMinutes(1));

    private static AuthMetadataReadinessProbe CreateProbe(
        string json,
        ApiOptions options,
        string mediaType = "application/json")
    {
        var handler = new StaticResponseHandler(json, mediaType);
        return new AuthMetadataReadinessProbe(new HttpClient(handler), options);
    }

    private sealed class StaticResponseHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
