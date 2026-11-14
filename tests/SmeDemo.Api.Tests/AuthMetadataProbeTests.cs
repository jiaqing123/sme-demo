using System.Net;
using System.Text;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Readiness;

namespace SmeDemo.Api.Tests;

/// <summary>
/// The real readiness probe is not covered by the endpoint tests, which replace
/// every probe with a fake. These tests exercise its document validation
/// directly, including the fields JwtBearer depends on.
/// </summary>
[TestClass]
public sealed class AuthMetadataProbeTests
{
    private static ApiOptions Options(bool requireHttps = false) =>
        ApiOptions.ForOpenApiDocumentGeneration() with
        {
            AuthMetadataUrl = "http://auth:8081/.well-known/openid-configuration",
            JwtIssuer = "http://auth:8081",
            RequireHttpsMetadata = requireHttps,
        };

    [TestMethod]
    public async Task Check_AcceptsMetadataForTheConfiguredIssuer()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/.well-known/jwks.json"}""",
            Options());

        Assert.IsTrue(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_AcceptsIssuerWithTrailingSlash()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081/","jwks_uri":"http://auth:8081/jwks"}""",
            Options());

        Assert.IsTrue(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsMetadataForAnotherIssuer()
    {
        var probe = CreateProbe(
            """{"issuer":"https://evil.example","jwks_uri":"http://auth:8081/jwks"}""",
            Options());

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsMetadataWithoutJwksUri()
    {
        var probe = CreateProbe("""{"issuer":"http://auth:8081"}""", Options());

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsRelativeJwksUri()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"/.well-known/jwks.json"}""",
            Options());

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsPlainHttpJwksWhenMetadataRequiresHttps()
    {
        var probe = CreateProbe(
            """{"issuer":"http://auth:8081","jwks_uri":"http://auth:8081/jwks"}""",
            Options(requireHttps: true));

        Assert.IsFalse(await probe.CheckAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Check_RejectsMetadataThatIsNotAJsonObject()
    {
        var array = CreateProbe("""["not","metadata"]""", Options());
        var nonJson = CreateProbe("""{"issuer":"http://auth:8081"}""", Options(), "text/html");

        Assert.IsFalse(await array.CheckAsync(CancellationToken.None));
        Assert.IsFalse(await nonJson.CheckAsync(CancellationToken.None));
    }

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
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
