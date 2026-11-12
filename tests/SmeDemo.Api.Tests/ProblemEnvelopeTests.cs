using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Focused checks for the §7.2 problem envelope. Every error response the API
/// produces must carry the request path in <c>instance</c>, a non-empty
/// <c>traceId</c> extension and the <c>application/problem+json</c> content type.
/// </summary>
[TestClass]
public sealed class ProblemEnvelopeTests
{
    private const string ProblemJson = "application/problem+json";

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");

    [TestMethod]
    public async Task ErrorResponses_CarryInstanceTraceIdAndProblemJson_For400401404And405()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        var cases = new[]
        {
            (Scenario: "validation failure", Method: HttpMethod.Get, Path: $"/api/v1/me/cards/{FirstCardId}/transactions?limit=abc", Token: (string?)token, Status: HttpStatusCode.BadRequest, Code: "VALIDATION_ERROR"),
            (Scenario: "missing token", Method: HttpMethod.Get, Path: "/api/v1/me", Token: (string?)null, Status: HttpStatusCode.Unauthorized, Code: "UNAUTHENTICATED"),
            (Scenario: "unknown route", Method: HttpMethod.Get, Path: "/api/v1/does-not-exist", Token: (string?)null, Status: HttpStatusCode.NotFound, Code: "NOT_FOUND"),
            (Scenario: "method mismatch", Method: HttpMethod.Put, Path: "/api/v1/me", Token: (string?)null, Status: HttpStatusCode.MethodNotAllowed, Code: "METHOD_NOT_ALLOWED"),
            (Scenario: "method mismatch on anonymous route", Method: HttpMethod.Post, Path: "/healthz", Token: (string?)null, Status: HttpStatusCode.MethodNotAllowed, Code: "METHOD_NOT_ALLOWED"),
        };

        foreach (var testCase in cases)
        {
            var error = await SendAsync(client, testCase.Method, testCase.Path, testCase.Token);

            AssertProblemEnvelope(
                error,
                testCase.Status,
                testCase.Code,
                testCase.Scenario);
        }
    }

    [TestMethod]
    public async Task MethodMismatch_OnProtectedRoute_Returns405Not401()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        // An anonymous call to a protected path with the wrong verb must reach
        // routing, not the authentication challenge.
        var error = await SendAsync(client, HttpMethod.Put, "/api/v1/me", token: null);

        Assert.AreEqual(
            HttpStatusCode.MethodNotAllowed,
            error.Status,
            $"A method mismatch must be 405, not 401. Body: {error.Body}");
        AssertProblemEnvelope(error, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED", "anonymous PUT /api/v1/me");
    }

    [TestMethod]
    public async Task ServiceUnavailable_CarriesEnvelopeAndRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        factory.Transactions.Unavailable = true;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/me/cards/{FirstCardId}/transactions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var error = new ErrorResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body,
            request.RequestUri!.AbsolutePath);

        AssertProblemEnvelope(error, HttpStatusCode.ServiceUnavailable, "UPSTREAM_UNAVAILABLE", "unavailable store");
        Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            response.Headers.RetryAfter?.Delta,
            $"A 503 must tell the caller how long to wait. Body: {body}");
    }

    [TestMethod]
    public async Task MetadataFetchFailure_Returns503UpstreamUnavailable_NotA401InvalidToken()
    {
        // This factory leaves the production bearer configuration in place and
        // points it at a closed loopback port. A validly signed token must still
        // not be reported as invalid when the authority could not be reached.
        using var factory = new ApiTestFactory(unreachableAuthMetadata: true);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken());

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var error = new ErrorResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body,
            request.RequestUri!.AbsolutePath);

        AssertProblemEnvelope(
            error,
            HttpStatusCode.ServiceUnavailable,
            "UPSTREAM_UNAVAILABLE",
            "unreachable auth metadata");
        Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            response.Headers.RetryAfter?.Delta,
            $"A metadata-fetch failure must carry the standard retry hint. Body: {body}");
    }

    [TestMethod]
    public async Task InvalidToken_OnAHealthyMetadataHost_StillReturns401InvalidToken()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        // Regression guard: signature and format failures must keep the 401
        // mapping and never be reclassified as an upstream outage.
        var error = await SendAsync(client, HttpMethod.Get, "/api/v1/me", "not-a-jwt");

        AssertProblemEnvelope(error, HttpStatusCode.Unauthorized, "INVALID_TOKEN", "malformed token");
    }

    private static async Task<ErrorResponse> SendAsync(
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

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new ErrorResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body,
            request.RequestUri!.AbsolutePath);
    }

    private static void AssertProblemEnvelope(
        ErrorResponse error,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string scenario)
    {
        Assert.AreEqual(
            expectedStatus,
            error.Status,
            $"{scenario}: unexpected status. Body: {error.Body}");
        Assert.AreEqual(
            ProblemJson,
            error.MediaType,
            $"{scenario}: every problem response must use the problem+json content type. Body: {error.Body}");

        using var document = JsonDocument.Parse(error.Body);
        var root = document.RootElement;

        Assert.AreEqual(
            (int)expectedStatus,
            root.GetProperty("status").GetInt32(),
            $"{scenario}: the envelope status must match the HTTP status. Body: {error.Body}");
        Assert.AreEqual(
            error.Path,
            root.GetProperty("instance").GetString(),
            $"{scenario}: instance must be the request path. Body: {error.Body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()),
            $"{scenario}: the envelope must carry a non-empty traceId. Body: {error.Body}");
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("error").GetProperty("code").GetString(),
            $"{scenario}: unexpected error.code. Body: {error.Body}");
    }

    private sealed record ErrorResponse(
        HttpStatusCode Status,
        string? MediaType,
        string Body,
        string Path);
}
