using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// The complete status-code matrix for the auth service in one auditable place.
/// Every error response is checked against the same §7.2 contract as the
/// resource API: exact HTTP status, <c>application/problem+json</c>,
/// <c>error.code</c>, a non-empty <c>error.message</c>, <c>traceId</c>,
/// <c>instance</c> equal to the request path, no implementation detail,
/// <c>nosniff</c> and, where the status defines it, a numeric
/// <c>Retry-After</c>.
///
/// <para>
/// 403 is deliberately absent: this service has no authorization layer by
/// design (every route is anonymous and its only caller-facing identity check is
/// the bearer-token validation inside <c>/auth/userinfo</c>, which returns 401).
/// The API matrix covers the 403 FORBIDDEN contract for the cross-service
/// envelope. No 403 case is invented here.
/// </para>
///
/// The exact root/error property-set assertion is the other half of the
/// cross-service consistency check: <c>SmeDemo.Api.Tests.StatusCodeMatrixTests</c>
/// asserts the identical sets for the same status/code pairs.
/// </summary>
[TestClass]
public sealed class AuthStatusCodeMatrixTests
{
    private const string ProblemJson = "application/problem+json";
    private const string Nosniff = "nosniff";
    private const string ForcedFailureMessage = "forced plain SMS failure";

    // ---------------------------------------------------------------------
    // 400
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task MalformedJsonAndMissingCredentials_Return400ValidationError()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        using var malformedBody = new StringContent(
            "{\"username\":",
            Encoding.UTF8,
            "application/json");
        var malformed = await SendAsync(client, HttpMethod.Post, "/auth/login", malformedBody);
        AssertErrorContract(
            malformed,
            HttpStatusCode.BadRequest,
            "VALIDATION_ERROR",
            allowValidationErrors: true,
            scenario: "malformed JSON");

        var missingUsername = await LoginAsync(
            client,
            username: null,
            password: AuthTestFactory.DemoPassword);
        AssertErrorContract(
            missingUsername,
            HttpStatusCode.BadRequest,
            "VALIDATION_ERROR",
            allowValidationErrors: true,
            scenario: "missing username");

        var missingPassword = await LoginAsync(
            client,
            username: AuthTestFactory.DemoUsername,
            password: null);
        AssertErrorContract(
            missingPassword,
            HttpStatusCode.BadRequest,
            "VALIDATION_ERROR",
            allowValidationErrors: true,
            scenario: "missing password");
    }

    // ---------------------------------------------------------------------
    // 401
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownUsernameAndWrongPassword_ReturnIndistinguishable401WithoutSms()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        var unknown = await LoginAsync(
            client,
            "no-such-user",
            AuthTestFactory.DemoPassword);
        var wrongPassword = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            "not-the-password");

        AssertErrorContract(
            unknown,
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            scenario: "unknown username");
        AssertErrorContract(
            wrongPassword,
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            scenario: "wrong password");

        Assert.AreEqual(
            ReadErrorMessage(unknown),
            ReadErrorMessage(wrongPassword),
            "Unknown-user and wrong-password failures must be indistinguishable.");
        Assert.AreEqual(
            0,
            factory.FakeSms.SendCount,
            "A failed login must not send an OTP.");
    }

    [TestMethod]
    public async Task WrongOtp_Returns401Unauthenticated()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();
        var challengeId = await LoginAndGetChallengeIdAsync(client);

        var error = await VerifyAsync(client, challengeId, "000000");

        AssertErrorContract(error, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [TestMethod]
    public async Task UserInfoWithoutOrWithMalformedToken_Returns401()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        var missing = await SendAsync(client, HttpMethod.Get, "/auth/userinfo");
        AssertErrorContract(
            missing,
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            scenario: "missing userinfo token");

        var malformed = await SendAsync(
            client,
            HttpMethod.Get,
            "/auth/userinfo",
            token: "not-a-jwt");
        AssertErrorContract(
            malformed,
            HttpStatusCode.Unauthorized,
            "INVALID_TOKEN",
            scenario: "malformed userinfo token");
    }

    // ---------------------------------------------------------------------
    // 404
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownRoute_Returns404NotFound()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(client, HttpMethod.Get, "/auth/no-such-route");

        AssertErrorContract(error, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    /// <summary>
    /// The shared-envelope assertion for a status/code pair both services produce.
    /// The API matrix asserts this exact set for its own 404 NOT_FOUND response.
    /// </summary>
    [TestMethod]
    public async Task NotFoundEnvelope_UsesTheSamePropertyNamesAndNestingAsApi()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(client, HttpMethod.Get, "/auth/no-such-route");

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
    public async Task MethodMismatch_Returns405MethodNotAllowed()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();

        var error = await SendAsync(client, HttpMethod.Get, "/auth/login");

        AssertErrorContract(error, HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
    }

    // ---------------------------------------------------------------------
    // 429
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task AccountLoginBudget_Returns429RateLimitedWithRetryAfter()
    {
        using var factory = new AuthTestFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_LOGIN_IP"] = "100",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT"] = "1",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW"] = "5m",
        });
        using var client = factory.CreateClient();

        var permitted = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            AuthTestFactory.DemoPassword);
        Assert.AreEqual(
            HttpStatusCode.OK,
            permitted.Status,
            $"The first login must be permitted so the matrix observes a real 429. Body: {permitted.Body}");

        var limited = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            AuthTestFactory.DemoPassword);
        AssertErrorContract(
            limited,
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED",
            expectRetryAfter: true);
    }

    [TestMethod]
    public async Task ThreeWrongOtpAttempts_Return429RateLimitedFromTheChallengeStore()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();
        var challengeId = await LoginAndGetChallengeIdAsync(client);

        var first = await VerifyAsync(client, challengeId, "000000");
        AssertErrorContract(
            first,
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            scenario: "first wrong OTP");

        var second = await VerifyAsync(client, challengeId, "000000");
        AssertErrorContract(
            second,
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            scenario: "second wrong OTP");

        // The third attempt exhausts ChallengeStore's three-attempt budget and
        // destroys the challenge; the 429 still tells a client how soon to try.
        var third = await VerifyAsync(client, challengeId, "000000");
        AssertErrorContract(
            third,
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED",
            expectRetryAfter: true,
            scenario: "third wrong OTP");
    }

    // ---------------------------------------------------------------------
    // 500
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task PlainSmsFailure_Returns500InternalWithoutLeakingDetail()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();
        factory.FakeSms.ThrowOnSend = new InvalidOperationException(ForcedFailureMessage);

        var error = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            AuthTestFactory.DemoPassword);

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
    public async Task SmsDeliveryFailure_Returns503UpstreamUnavailableWithRetryAfter()
    {
        using var factory = new AuthTestFactory();
        using var client = factory.CreateClient();
        factory.FakeSms.Failure = new SmsDeliveryException("provider unavailable");

        var error = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            AuthTestFactory.DemoPassword);

        AssertErrorContract(
            error,
            HttpStatusCode.ServiceUnavailable,
            "UPSTREAM_UNAVAILABLE",
            expectRetryAfter: true);
    }

    // ---------------------------------------------------------------------
    // Shared contract assertions
    // ---------------------------------------------------------------------

    private static Task<ErrorResponse> LoginAsync(
        HttpClient client,
        string? username,
        string? password) =>
        SendAsync(
            client,
            HttpMethod.Post,
            "/auth/login",
            JsonContent.Create(new { username, password }));

    private static Task<ErrorResponse> VerifyAsync(
        HttpClient client,
        string challengeId,
        string code) =>
        SendAsync(
            client,
            HttpMethod.Post,
            $"/auth/challenges/{challengeId}/verify",
            JsonContent.Create(new { code }));

    private static async Task<string> LoginAndGetChallengeIdAsync(HttpClient client)
    {
        var login = await LoginAsync(
            client,
            AuthTestFactory.DemoUsername,
            AuthTestFactory.DemoPassword);
        Assert.AreEqual(
            HttpStatusCode.OK,
            login.Status,
            $"The login must succeed before the verify matrix can run. Body: {login.Body}");

        using var document = JsonDocument.Parse(login.Body);
        return document.RootElement.GetProperty("challenge_id").GetString()!;
    }

    private static async Task<ErrorResponse> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpContent? content = null,
        string? token = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = content,
        };
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token);
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
            $"{label}: unexpected root property set; this must match the API service. Body: {body}");

        var expectedErrorProperties = allowValidationErrors
            ? new[] { "code", "message", "errors" }
            : new[] { "code", "message" };
        CollectionAssert.AreEquivalent(
            expectedErrorProperties,
            root.GetProperty("error").EnumerateObject().Select(property => property.Name).ToArray(),
            $"{label}: unexpected error property set; this must match the API service. Body: {body}");
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

    private static string? ReadErrorMessage(ErrorResponse error)
    {
        using var document = JsonDocument.Parse(error.Body);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    private sealed record ErrorResponse(
        HttpStatusCode Status,
        string? MediaType,
        string Body,
        string Path,
        string? RetryAfter,
        string? ContentTypeOptions);
}
