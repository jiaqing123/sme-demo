using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SmeDemo.Api.Tests;

/// <summary>
/// The complete status-code matrix for the resource API in one auditable place.
/// Every error response is checked against the same §7.2 contract: exact HTTP
/// status, <c>application/problem+json</c>, <c>error.code</c>, a non-empty
/// <c>error.message</c>, <c>traceId</c>, <c>instance</c> equal to the request
/// path, no implementation detail, and (where the status defines it) a numeric
/// <c>Retry-After</c>. Every error also carries <c>nosniff</c>.
///
/// The exact root/error property-set assertion in
/// <see cref="AssertSharedEnvelopeShape"/> is the cross-service consistency
/// check: <c>SmeDemo.Auth.Tests.AuthStatusCodeMatrixTests</c> asserts the
/// identical sets for the same status/code pairs, without either project booting
/// the other service's host.
/// </summary>
[TestClass]
public sealed class StatusCodeMatrixTests
{
    private const string ProblemJson = "application/problem+json";
    private const string Nosniff = "nosniff";
    private const string ForcedFailureMessage = "forced card repository failure";

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    private static readonly Guid UnknownCardId = Guid.Parse("c1000000-0000-4000-8000-0000000000ff");

    // ---------------------------------------------------------------------
    // 400
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task InvalidQuery_Returns400ValidationError()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=abc",
            factory.CreateToken());

        AssertErrorContract(
            error,
            HttpStatusCode.BadRequest,
            "VALIDATION_ERROR",
            allowValidationErrors: true);
    }

    // ---------------------------------------------------------------------
    // 401
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task TokenFailures_Return401WithTheirSpecificCodes()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "test-signing-key" };
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-60);

        var cases = new (string Scenario, string? Token, string Code)[]
        {
            ("missing token", null, "UNAUTHENTICATED"),
            (
                "expired token",
                factory.CreateToken(issuedAt: expiredAt, expiresAt: expiredAt.AddMinutes(30)),
                "TOKEN_EXPIRED"),
            ("malformed token", "not-a-jwt", "INVALID_TOKEN"),
            (
                "wrong-signature token",
                factory.CreateTokenWith(
                    signingCredentials: new SigningCredentials(
                        otherKey,
                        SecurityAlgorithms.RsaSha256)),
                "INVALID_TOKEN"),
        };

        foreach (var testCase in cases)
        {
            var error = await SendAsync(client, HttpMethod.Get, "/api/v1/me", testCase.Token);

            AssertErrorContract(
                error,
                HttpStatusCode.Unauthorized,
                testCase.Code,
                scenario: testCase.Scenario);
        }
    }

    // ---------------------------------------------------------------------
    // 403
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task TokenWithWrongOrg_Returns403Forbidden()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/me",
            factory.CreateToken(org: "other"));

        AssertErrorContract(error, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // ---------------------------------------------------------------------
    // 404
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownRouteAndUnknownCard_Return404NotFound()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var unknownRoute = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/does-not-exist",
            token: null);
        AssertErrorContract(unknownRoute, HttpStatusCode.NotFound, "NOT_FOUND");

        var unknownCard = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/v1/me/cards/{UnknownCardId}/transactions",
            factory.CreateToken());
        AssertErrorContract(unknownCard, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    /// <summary>
    /// The shared-envelope assertion for a status/code pair both services produce.
    /// The auth matrix asserts this exact set for its own 404 NOT_FOUND response.
    /// </summary>
    [TestMethod]
    public async Task NotFoundEnvelope_UsesTheSamePropertyNamesAndNestingAsAuth()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/does-not-exist",
            token: null);

        AssertErrorContract(error, HttpStatusCode.NotFound, "NOT_FOUND");

        using var document = JsonDocument.Parse(error.Body);
        var root = document.RootElement;

        CollectionAssert.AreEquivalent(
            new[] { "status", "title", "type", "detail", "instance", "traceId", "error" },
            root.EnumerateObject().Select(property => property.Name).ToArray(),
            $"The shared envelope must expose exactly these root properties. Body: {error.Body}");
        CollectionAssert.AreEquivalent(
            new[] { "code", "message" },
            root.GetProperty("error").EnumerateObject().Select(property => property.Name).ToArray(),
            $"The shared envelope must nest code and message under error. Body: {error.Body}");
    }

    // ---------------------------------------------------------------------
    // 405
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task MethodMismatch_Returns405Not401_ForAnonymousAndAuthenticatedCallers()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        foreach (var candidate in new string?[] { null, token })
        {
            var error = await SendAsync(client, HttpMethod.Put, "/api/v1/me", candidate);

            AssertErrorContract(
                error,
                HttpStatusCode.MethodNotAllowed,
                "METHOD_NOT_ALLOWED",
                scenario: candidate is null ? "anonymous PUT" : "authenticated PUT");
        }
    }

    // ---------------------------------------------------------------------
    // 429
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task ExhaustedBudget_Returns429RateLimitedWithRetryAfter()
    {
        using var factory = new ApiTestFactory(
            rateLimitPermit: 1,
            rateLimitWindow: TimeSpan.FromMinutes(1));
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        var permitted = await SendAsync(client, HttpMethod.Get, "/api/v1/me", token);
        Assert.AreEqual(
            HttpStatusCode.OK,
            permitted.Status,
            $"The first request must be permitted so the matrix observes a real 429. Body: {permitted.Body}");

        var limited = await SendAsync(client, HttpMethod.Get, "/api/v1/me", token);
        AssertErrorContract(
            limited,
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED",
            expectRetryAfter: true);
    }

    // ---------------------------------------------------------------------
    // 500
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task UnhandledRepositoryFailure_Returns500InternalWithoutLeakingDetail()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        factory.Cards.Failure = new InvalidOperationException(ForcedFailureMessage);

        var error = await SendAsync(
            client,
            HttpMethod.Get,
            "/api/v1/me/cards",
            factory.CreateToken());

        AssertErrorContract(
            error,
            HttpStatusCode.InternalServerError,
            "INTERNAL",
            forbiddenText: ForcedFailureMessage);
    }

    // ---------------------------------------------------------------------
    // 503
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task UnavailableTransactionStore_Returns503UpstreamUnavailableWithRetryAfter()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        factory.Transactions.Unavailable = true;

        var error = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/v1/me/cards/{FirstCardId}/transactions",
            factory.CreateToken());

        AssertErrorContract(
            error,
            HttpStatusCode.ServiceUnavailable,
            "UPSTREAM_UNAVAILABLE",
            expectRetryAfter: true);
    }

    // ---------------------------------------------------------------------
    // Shared contract assertions
    // ---------------------------------------------------------------------

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
            request.RequestUri!.AbsolutePath,
            response.Headers.TryGetValues("Retry-After", out var retryAfter)
                ? retryAfter.Single()
                : null,
            response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions)
                ? contentTypeOptions.Single()
                : null);
    }

    private static void AssertErrorContract(
        ErrorResponse error,
        HttpStatusCode expectedStatus,
        string expectedCode,
        bool expectRetryAfter = false,
        bool allowValidationErrors = false,
        string? forbiddenText = null,
        string? scenario = null)
    {
        var label = scenario ?? $"{expectedStatus} {expectedCode}";

        Assert.AreEqual(
            expectedStatus,
            error.Status,
            $"{label}: unexpected HTTP status. Body: {error.Body}");
        Assert.AreEqual(
            ProblemJson,
            error.MediaType,
            $"{label}: every error must use the problem+json content type. Body: {error.Body}");
        Assert.AreEqual(
            Nosniff,
            error.ContentTypeOptions,
            $"{label}: every error must disable MIME sniffing. Body: {error.Body}");
        AssertNoImplementationDetail(error.Body, label, forbiddenText);

        if (expectRetryAfter)
        {
            AssertPositiveRetryAfter(error, label);
        }

        using var document = JsonDocument.Parse(error.Body);
        var root = document.RootElement;

        Assert.AreEqual(
            (int)expectedStatus,
            root.GetProperty("status").GetInt32(),
            $"{label}: the envelope status must match the HTTP status. Body: {error.Body}");
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("title").GetString(),
            $"{label}: the envelope title must be the error code. Body: {error.Body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()),
            $"{label}: the envelope must carry a type URI. Body: {error.Body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()),
            $"{label}: the envelope must carry a detail message. Body: {error.Body}");
        Assert.AreEqual(
            error.Path,
            root.GetProperty("instance").GetString(),
            $"{label}: instance must be the request path. Body: {error.Body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()),
            $"{label}: the envelope must carry a non-empty traceId. Body: {error.Body}");
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("error").GetProperty("code").GetString(),
            $"{label}: unexpected error.code. Body: {error.Body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("error").GetProperty("message").GetString()),
            $"{label}: error.message must be non-empty. Body: {error.Body}");

        AssertSharedEnvelopeShape(root, allowValidationErrors, label, error.Body);
    }

    private static void AssertSharedEnvelopeShape(
        JsonElement root,
        bool allowValidationErrors,
        string label,
        string body)
    {
        CollectionAssert.AreEquivalent(
            new[] { "status", "title", "type", "detail", "instance", "traceId", "error" },
            root.EnumerateObject().Select(property => property.Name).ToArray(),
            $"{label}: unexpected root property set; this must match the auth service. Body: {body}");

        var expectedErrorProperties = allowValidationErrors
            ? new[] { "code", "message", "errors" }
            : new[] { "code", "message" };
        CollectionAssert.AreEquivalent(
            expectedErrorProperties,
            root.GetProperty("error").EnumerateObject().Select(property => property.Name).ToArray(),
            $"{label}: unexpected error property set; this must match the auth service. Body: {body}");
    }

    private static void AssertPositiveRetryAfter(ErrorResponse error, string label)
    {
        Assert.IsNotNull(
            error.RetryAfter,
            $"{label}: a retryable failure must carry Retry-After. Body: {error.Body}");
        Assert.IsTrue(
            int.TryParse(
                error.RetryAfter,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds),
            $"{label}: Retry-After must be numeric seconds, was '{error.RetryAfter}'. Body: {error.Body}");
        Assert.IsTrue(
            seconds > 0,
            $"{label}: Retry-After must be positive, was '{error.RetryAfter}'. Body: {error.Body}");
    }

    private static void AssertNoImplementationDetail(
        string body,
        string label,
        string? forbiddenText)
    {
        Assert.IsFalse(
            body.Contains("stackTrace", StringComparison.OrdinalIgnoreCase),
            $"{label}: the response must not contain a stack trace. Body: {body}");
        Assert.IsFalse(
            body.Contains("Exception", StringComparison.Ordinal),
            $"{label}: the response must not contain an exception type name. Body: {body}");
        Assert.IsFalse(
            body.Contains(" at SmeDemo.", StringComparison.Ordinal),
            $"{label}: the response must not contain a stack frame. Body: {body}");
        Assert.IsFalse(
            body.Contains(".cs", StringComparison.OrdinalIgnoreCase),
            $"{label}: the response must not contain a source file path. Body: {body}");

        if (forbiddenText is not null)
        {
            Assert.IsFalse(
                body.Contains(forbiddenText, StringComparison.Ordinal),
                $"{label}: the response must not echo the internal failure message. Body: {body}");
        }
    }

    private sealed record ErrorResponse(
        HttpStatusCode Status,
        string? MediaType,
        string Body,
        string Path,
        string? RetryAfter,
        string? ContentTypeOptions);
}
