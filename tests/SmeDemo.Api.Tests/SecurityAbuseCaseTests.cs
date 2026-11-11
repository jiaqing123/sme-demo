using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SmeDemo.Api.Tests;

/// <summary>
/// The executable form of the abuse cases in docs/threat-model.md §5 and the
/// security release gate in docs/reference/definition-of-done.md §4: object-level
/// authorisation, the negative token matrix, malformed input, log redaction, and
/// the rule that errors never leak implementation detail.
/// </summary>
[TestClass]
public sealed class SecurityAbuseCaseTests
{
    private const string ProblemJson = "application/problem+json";
    private const string DemoPhone = "+61400000000";
    private const string DemoPassword = "Demo_Passw0rd!";
    private const string DemoOtp = "123456";

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherCardId = Guid.Parse("c1000000-0000-4000-8000-000000000099");
    private static readonly Guid UnknownCardId = Guid.Parse("c1000000-0000-4000-8000-0000000000ff");

    // ---------------------------------------------------------------------
    // threat-model.md §5.3 and S4: IDOR/BOLA on card artwork
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Bola_CardArt_OtherUsersCardIsForbidden_UnknownCardIsNotFound_WithoutExistenceLeak()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        var foreign = await GetErrorAsync(
            client,
            $"/api/v1/cards/{OtherCardId}/art",
            token);

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            foreign.Status,
            $"Another user's card artwork must return 403 Forbidden. Body: {foreign.Body}");
        Assert.AreEqual(
            "FORBIDDEN",
            foreign.Code,
            $"Another user's card artwork must use error.code FORBIDDEN. Body: {foreign.Body}");
        Assert.AreEqual(
            ProblemJson,
            foreign.MediaType,
            $"Another user's card artwork must use the problem+json envelope. Body: {foreign.Body}");
        AssertNoObjectExistenceLeak(foreign.Body, OtherCardId, "other user's card artwork");
        AssertNoStackTrace(foreign.Body, "other user's card artwork");

        var unknown = await GetErrorAsync(
            client,
            $"/api/v1/cards/{UnknownCardId}/art",
            token);

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            unknown.Status,
            $"An unknown card id must return 404 Not Found. Body: {unknown.Body}");
        Assert.AreEqual(
            "NOT_FOUND",
            unknown.Code,
            $"An unknown card id must use error.code NOT_FOUND. Body: {unknown.Body}");
        Assert.AreEqual(
            ProblemJson,
            unknown.MediaType,
            $"An unknown card id must use the problem+json envelope. Body: {unknown.Body}");
        AssertNoObjectExistenceLeak(unknown.Body, UnknownCardId, "unknown card artwork");
        AssertNoStackTrace(unknown.Body, "unknown card artwork");
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.3 and S4: IDOR/BOLA on transactions
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Bola_Transactions_OtherUsersCardIsForbidden_UnknownCardIsNotFound_WithStandardProblemEnvelope()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        var foreign = await GetErrorAsync(
            client,
            $"/api/v1/me/cards/{OtherCardId}/transactions",
            token);

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            foreign.Status,
            $"Another user's transactions must return 403 Forbidden. Body: {foreign.Body}");
        Assert.AreEqual(
            "FORBIDDEN",
            foreign.Code,
            $"Another user's transactions must use error.code FORBIDDEN. Body: {foreign.Body}");
        Assert.AreEqual(
            ProblemJson,
            foreign.MediaType,
            $"Another user's transactions must use the problem+json envelope. Body: {foreign.Body}");
        AssertNoObjectExistenceLeak(foreign.Body, OtherCardId, "other user's transactions");
        AssertNoStackTrace(foreign.Body, "other user's transactions");

        var unknown = await GetErrorAsync(
            client,
            $"/api/v1/me/cards/{UnknownCardId}/transactions",
            token);

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            unknown.Status,
            $"An unknown card id must return 404 Not Found. Body: {unknown.Body}");
        Assert.AreEqual(
            "NOT_FOUND",
            unknown.Code,
            $"An unknown card id must use error.code NOT_FOUND. Body: {unknown.Body}");
        Assert.AreEqual(
            ProblemJson,
            unknown.MediaType,
            $"An unknown card id must use the problem+json envelope. Body: {unknown.Body}");
        AssertStandardProblemEnvelope(unknown.Body, HttpStatusCode.NotFound, "NOT_FOUND");
        AssertNoObjectExistenceLeak(unknown.Body, UnknownCardId, "unknown card transactions");
        AssertNoStackTrace(unknown.Body, "unknown card transactions");
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.3 and S4: no cross-tenant leakage in the collection
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Bola_CardsList_ContainsOnlyTheCallersCards()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = Bearer(factory.CreateToken());

        var response = await client.GetAsync("/api/v1/me/cards");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            $"The caller's card list must be readable. Body: {body}");

        using var document = JsonDocument.Parse(body);
        var ids = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "c1000000-0000-4000-8000-000000000001",
                "c1000000-0000-4000-8000-000000000002",
                "c1000000-0000-4000-8000-000000000003",
            },
            ids,
            $"The cards list must contain exactly the caller's three cards. Body: {body}");

        Assert.IsFalse(
            ids.Contains(OtherCardId.ToString(), StringComparer.OrdinalIgnoreCase),
            $"The cards list must not contain another user's card. Body: {body}");
        Assert.IsFalse(
            ids.Contains("c1000000-0000-4000-8000-000000000099", StringComparer.OrdinalIgnoreCase),
            $"The cards list must not contain another user's card. Body: {body}");
        Assert.IsFalse(
            body.Contains("9999", StringComparison.Ordinal),
            $"The cards list must not expose another card's last four digits. Body: {body}");
        AssertNoStackTrace(body, "cards list");
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.4 and §5.5, S2: the token matrix
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task TokenMatrix_RejectsWrongOrgIssuerAudienceSignatureAlgorithmExpiryAndMissingTokens()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "test-signing-key" };
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-60);

        var cases = new[]
        {
            (Scenario: "wrong org", Token: factory.CreateToken(org: "other"), Status: HttpStatusCode.Forbidden, Code: "FORBIDDEN"),
            (Scenario: "wrong issuer", Token: factory.CreateTokenWith(issuer: "http://attacker:8081"), Status: HttpStatusCode.Unauthorized, Code: "INVALID_TOKEN"),
            (Scenario: "wrong audience", Token: factory.CreateTokenWith(audience: "another-api"), Status: HttpStatusCode.Unauthorized, Code: "INVALID_TOKEN"),
            (Scenario: "different RSA key", Token: factory.CreateTokenWith(signingCredentials: new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256)), Status: HttpStatusCode.Unauthorized, Code: "INVALID_TOKEN"),
            (Scenario: "expired token", Token: factory.CreateToken(issuedAt: expiredAt, expiresAt: expiredAt.AddMinutes(30)), Status: HttpStatusCode.Unauthorized, Code: "TOKEN_EXPIRED"),
            (Scenario: "missing token", Token: (string?)null, Status: HttpStatusCode.Unauthorized, Code: "UNAUTHENTICATED"),
            (Scenario: "malformed token", Token: "not-a-jwt", Status: HttpStatusCode.Unauthorized, Code: "INVALID_TOKEN"),
            (Scenario: "alg=none token", Token: factory.CreateTokenWith(unsigned: true), Status: HttpStatusCode.Unauthorized, Code: "INVALID_TOKEN"),
        };

        foreach (var testCase in cases)
        {
            var error = await GetErrorAsync(client, "/api/v1/me", testCase.Token);

            Assert.AreEqual(
                testCase.Status,
                error.Status,
                $"{testCase.Scenario}: expected {(int)testCase.Status} {testCase.Status}, got {(int)error.Status} {error.Status}. Body: {error.Body}");
            Assert.AreEqual(
                testCase.Code,
                error.Code,
                $"{testCase.Scenario}: expected error.code {testCase.Code}, got {error.Code ?? "<none>"}. Body: {error.Body}");
            Assert.AreEqual(
                ProblemJson,
                error.MediaType,
                $"{testCase.Scenario}: expected the problem+json envelope. Body: {error.Body}");
            AssertNoStackTrace(error.Body, testCase.Scenario);
        }
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.6: malformed input and unknown routes keep the envelope
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task RequestValidation_MalformedQueryAndUnknownRouteReturnProblemEnvelopeWithoutImplementationDetail()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var token = factory.CreateToken();

        var malformedQuery = await GetErrorAsync(
            client,
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=abc",
            token);

        Assert.AreEqual(
            HttpStatusCode.BadRequest,
            malformedQuery.Status,
            $"A non-numeric limit must return 400 Bad Request. Body: {malformedQuery.Body}");
        Assert.AreEqual(
            "VALIDATION_ERROR",
            malformedQuery.Code,
            $"A non-numeric limit must use error.code VALIDATION_ERROR. Body: {malformedQuery.Body}");
        Assert.AreEqual(
            ProblemJson,
            malformedQuery.MediaType,
            $"A non-numeric limit must use the problem+json envelope. Body: {malformedQuery.Body}");
        AssertNoStackTrace(malformedQuery.Body, "malformed limit query");

        var unknownRoute = await GetErrorAsync(client, "/api/v1/does-not-exist", token: null);

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            unknownRoute.Status,
            $"An unknown route must return 404 Not Found. Body: {unknownRoute.Body}");
        Assert.AreEqual(
            "NOT_FOUND",
            unknownRoute.Code,
            $"An unknown route must use error.code NOT_FOUND. Body: {unknownRoute.Body}");
        Assert.AreEqual(
            ProblemJson,
            unknownRoute.MediaType,
            $"An unknown route must use the problem+json envelope. Body: {unknownRoute.Body}");
        AssertStandardProblemEnvelope(unknownRoute.Body, HttpStatusCode.NotFound, "NOT_FOUND");
        AssertNoStackTrace(unknownRoute.Body, "unknown route");
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.8 and S7: logs never contain secrets or PII
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Logs_DoNotContainBearerTokensDemoPhonePasswordOtpOrAuthorizationHeader()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        var profileToken = factory.CreateToken();
        var artworkToken = factory.CreateToken();

        // (i) An authenticated call: the token is in the request header.
        var me = await GetErrorAsync(client, "/api/v1/me", profileToken);
        Assert.AreEqual(
            HttpStatusCode.OK,
            me.Status,
            $"The authenticated /api/v1/me call must succeed. Body: {me.Body}");

        // (ii) A failed token call: the bearer value is invalid and rejected.
        var rejected = await GetErrorAsync(client, "/api/v1/me", "not-a-jwt");
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            rejected.Status,
            $"The malformed token must be rejected. Body: {rejected.Body}");

        // (iii) A 404 artwork call: the token reaches an error path.
        var artwork = await GetErrorAsync(client, $"/api/v1/cards/{UnknownCardId}/art", artworkToken);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            artwork.Status,
            $"The unknown card artwork call must return 404. Body: {artwork.Body}");

        var logs = factory.Logs.Text;
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(logs),
            "The capturing logger recorded nothing, so the secret scan would be vacuous.");

        Assert.IsFalse(
            logs.Contains(DemoPhone, StringComparison.Ordinal),
            $"Logs must never contain the demo phone number. Captured logs:{Environment.NewLine}{logs}");
        Assert.IsFalse(
            logs.Contains(DemoPassword, StringComparison.Ordinal),
            $"Logs must never contain the demo password. Captured logs:{Environment.NewLine}{logs}");
        Assert.IsFalse(
            logs.Contains(DemoOtp, StringComparison.Ordinal),
            $"Logs must never contain the demo OTP. Captured logs:{Environment.NewLine}{logs}");
        Assert.IsFalse(
            logs.Contains("Authorization: Bearer", StringComparison.OrdinalIgnoreCase),
            $"Logs must never contain an Authorization: Bearer header value. Captured logs:{Environment.NewLine}{logs}");
        AssertLogDoesNotContainToken(logs, profileToken, "authenticated /api/v1/me call");
        AssertLogDoesNotContainToken(logs, artworkToken, "404 card artwork call");
    }

    // ---------------------------------------------------------------------
    // threat-model.md §5.6 and S7: no implementation detail in any error
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task ErrorResponses_NeverCarryAStackTraceOrExceptionType()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "test-signing-key" };
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-60);
        var valid = factory.CreateToken();

        var cases = new[]
        {
            (Scenario: "card art for another user's card", Path: $"/api/v1/cards/{OtherCardId}/art", Token: (string?)valid, Status: HttpStatusCode.Forbidden),
            (Scenario: "card art for an unknown card", Path: $"/api/v1/cards/{UnknownCardId}/art", Token: valid, Status: HttpStatusCode.NotFound),
            (Scenario: "transactions for another user's card", Path: $"/api/v1/me/cards/{OtherCardId}/transactions", Token: valid, Status: HttpStatusCode.Forbidden),
            (Scenario: "transactions for an unknown card", Path: $"/api/v1/me/cards/{UnknownCardId}/transactions", Token: valid, Status: HttpStatusCode.NotFound),
            (Scenario: "malformed limit query", Path: $"/api/v1/me/cards/{FirstCardId}/transactions?limit=abc", Token: valid, Status: HttpStatusCode.BadRequest),
            (Scenario: "unknown route", Path: "/api/v1/does-not-exist", Token: (string?)null, Status: HttpStatusCode.NotFound),
            (Scenario: "wrong org", Path: "/api/v1/me", Token: factory.CreateToken(org: "other"), Status: HttpStatusCode.Forbidden),
            (Scenario: "wrong issuer", Path: "/api/v1/me", Token: factory.CreateTokenWith(issuer: "http://attacker:8081"), Status: HttpStatusCode.Unauthorized),
            (Scenario: "wrong audience", Path: "/api/v1/me", Token: factory.CreateTokenWith(audience: "another-api"), Status: HttpStatusCode.Unauthorized),
            (Scenario: "different RSA key", Path: "/api/v1/me", Token: factory.CreateTokenWith(signingCredentials: new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256)), Status: HttpStatusCode.Unauthorized),
            (Scenario: "expired token", Path: "/api/v1/me", Token: factory.CreateToken(issuedAt: expiredAt, expiresAt: expiredAt.AddMinutes(30)), Status: HttpStatusCode.Unauthorized),
            (Scenario: "missing token", Path: "/api/v1/me", Token: (string?)null, Status: HttpStatusCode.Unauthorized),
            (Scenario: "malformed token", Path: "/api/v1/me", Token: "not-a-jwt", Status: HttpStatusCode.Unauthorized),
            (Scenario: "alg=none token", Path: "/api/v1/me", Token: factory.CreateTokenWith(unsigned: true), Status: HttpStatusCode.Unauthorized),
        };

        foreach (var testCase in cases)
        {
            var error = await GetErrorAsync(client, testCase.Path, testCase.Token);
            Assert.AreEqual(
                testCase.Status,
                error.Status,
                $"{testCase.Scenario}: expected {(int)testCase.Status} {testCase.Status}, got {(int)error.Status} {error.Status}. Body: {error.Body}");
            AssertNoStackTrace(error.Body, testCase.Scenario);
        }

        factory.Transactions.Unavailable = true;
        var unavailable = await GetErrorAsync(
            client,
            $"/api/v1/me/cards/{FirstCardId}/transactions",
            valid);
        Assert.AreEqual(
            HttpStatusCode.ServiceUnavailable,
            unavailable.Status,
            $"An unavailable store must return 503. Body: {unavailable.Body}");
        AssertNoStackTrace(unavailable.Body, "unavailable transaction store");
    }

    // ---------------------------------------------------------------------
    // Assertion helpers
    // ---------------------------------------------------------------------

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static async Task<ErrorResponse> GetErrorAsync(
        HttpClient client,
        string path,
        string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = Bearer(token);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new ErrorResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body,
            TryReadErrorCode(body));
    }

    private static string? TryReadErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AssertStandardProblemEnvelope(
        string body,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.AreEqual(
            (int)expectedStatus,
            root.GetProperty("status").GetInt32(),
            $"The problem envelope must carry status {(int)expectedStatus}. Body: {body}");
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("title").GetString(),
            $"The problem envelope must carry title {expectedCode}. Body: {body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()),
            $"The problem envelope must carry a type URI. Body: {body}");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()),
            $"The problem envelope must carry a detail message. Body: {body}");
        Assert.AreEqual(
            expectedCode,
            root.GetProperty("error").GetProperty("code").GetString(),
            $"The problem envelope must carry error.code {expectedCode}. Body: {body}");
    }

    private static void AssertNoObjectExistenceLeak(
        string body,
        Guid requestedCardId,
        string scenario)
    {
        // §5.3 allows the two status codes to differ (403 for another user's
        // object, 404 for an unknown id) but forbids the body from adding an
        // existence signal of its own. Both messages may name "card" as the
        // resource type; neither may echo the identifier, artwork blob name, or
        // another card's last four digits.
        Assert.IsFalse(
            body.Contains(requestedCardId.ToString(), StringComparison.OrdinalIgnoreCase),
            $"{scenario}: the response must not echo the requested card id. Body: {body}");
        Assert.IsFalse(
            body.Contains("other.svg", StringComparison.OrdinalIgnoreCase),
            $"{scenario}: the response must not expose the artwork blob name. Body: {body}");
        Assert.IsFalse(
            body.Contains("9999", StringComparison.Ordinal),
            $"{scenario}: the response must not expose another card's last four digits. Body: {body}");
    }

    private static void AssertNoStackTrace(string body, string scenario)
    {
        Assert.IsFalse(
            body.Contains("stackTrace", StringComparison.OrdinalIgnoreCase),
            $"{scenario}: the response must not contain a stack trace. Body: {body}");
        Assert.IsFalse(
            body.Contains("at SmeDemo.", StringComparison.Ordinal),
            $"{scenario}: the response must not contain a stack frame. Body: {body}");
        Assert.IsFalse(
            body.Contains("Exception", StringComparison.Ordinal),
            $"{scenario}: the response must not contain an exception type name. Body: {body}");
        Assert.IsFalse(
            body.Contains(".cs", StringComparison.OrdinalIgnoreCase),
            $"{scenario}: the response must not contain a source file path. Body: {body}");
    }

    private static void AssertLogDoesNotContainToken(string logs, string token, string scenario)
    {
        Assert.IsFalse(
            logs.Contains(token, StringComparison.Ordinal),
            $"{scenario}: the captured logs must not contain the full bearer token. Captured logs:{Environment.NewLine}{logs}");

        // The task requires no substring longer than 20 characters. Scan every
        // 24-character window so a partial log leak cannot slip through.
        const int windowLength = 24;
        for (var start = 0; start + windowLength <= token.Length; start++)
        {
            var fragment = token.Substring(start, windowLength);
            Assert.IsFalse(
                logs.Contains(fragment, StringComparison.Ordinal),
                $"{scenario}: the captured logs must not contain a bearer-token fragment of more than 20 characters; found '{fragment}' at offset {start}. Captured logs:{Environment.NewLine}{logs}");
        }
    }

    private sealed record ErrorResponse(
        HttpStatusCode Status,
        string? MediaType,
        string Body,
        string? Code);
}
