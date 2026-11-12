using System.Net;
using System.Net.Http.Headers;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Threat-model §5.9 and the required-controls "Headers" row: every API
/// response must refuse MIME sniffing, and the SVG artwork response must be
/// sandboxed in case a browser opens it as a document.
/// </summary>
[TestClass]
public sealed class SecurityHeaderTests
{
    private const string Nosniff = "nosniff";
    private const string ArtContentSecurityPolicy = "default-src 'none'; sandbox";

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");

    [TestMethod]
    public async Task Nosniff_IsPresentOnSuccessAndOn401404And405Errors()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        using var ok = await SendAsync(client, HttpMethod.Get, "/api/v1/me", token);
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, await ok.Content.ReadAsStringAsync());
        AssertNosniff(ok, "200 /api/v1/me");

        using var unauthorized = await SendAsync(client, HttpMethod.Get, "/api/v1/me", token: null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        AssertNosniff(unauthorized, "401 /api/v1/me");

        using var notFound = await SendAsync(client, HttpMethod.Get, "/api/v1/does-not-exist", token: null);
        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
        AssertNosniff(notFound, "404 unknown route");

        using var methodNotAllowed = await SendAsync(client, HttpMethod.Put, "/api/v1/me", token: null);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, methodNotAllowed.StatusCode);
        AssertNosniff(methodNotAllowed, "405 wrong verb");
    }

    [TestMethod]
    public async Task Nosniff_IsPresentOn429And503Errors()
    {
        using var limited = new ApiTestFactory(
            rateLimitPermit: 1,
            rateLimitWindow: TimeSpan.FromMinutes(1));
        using var limitedClient = limited.CreateClient();
        limitedClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", limited.CreateToken());

        using var first = await limitedClient.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());

        using var rejected = await limitedClient.GetAsync("/api/v1/me");
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            rejected.StatusCode,
            await rejected.Content.ReadAsStringAsync());
        AssertNosniff(rejected, "429 rate limited");

        using var unavailable = new ApiTestFactory();
        using var unavailableClient = unavailable.CreateClient();
        unavailable.Transactions.Unavailable = true;

        using var error = await SendAsync(
            unavailableClient,
            HttpMethod.Get,
            $"/api/v1/me/cards/{FirstCardId}/transactions",
            unavailable.CreateToken());
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        AssertNosniff(error, "503 upstream unavailable");
    }

    [TestMethod]
    public async Task CardArt_Success_CarriesSvgContentTypeNosniffAndSandboxCsp()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var response = await client.GetAsync($"/api/v1/cards/{FirstCardId}/art");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            "image/svg+xml",
            response.Content.Headers.ContentType?.MediaType,
            "The artwork response must keep its SVG content type.");
        AssertNosniff(response, "card art 200");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.AreEqual(
            ArtContentSecurityPolicy,
            csp,
            "An SVG opened directly must not be able to run script.");

        // The existing cache policy is preserved alongside the new headers.
        Assert.IsTrue(response.Headers.CacheControl?.Private);
        Assert.AreEqual(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static void AssertNosniff(HttpResponseMessage response, string scenario)
    {
        Assert.IsTrue(
            response.Headers.TryGetValues("X-Content-Type-Options", out var values),
            $"{scenario}: the response must carry X-Content-Type-Options.");
        Assert.AreEqual(
            Nosniff,
            values!.Single(),
            $"{scenario}: the response must disable MIME sniffing.");
    }
}
