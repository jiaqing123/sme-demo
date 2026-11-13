using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Auth.Challenges;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// HTTP-level contract tests for the auth service. Each test boots a real host
/// through <see cref="AuthTestFactory"/> (in-memory database, fake SMS gateway,
/// manual clock) and asserts the wire shape and side effects of the login,
/// verify, resend and userinfo endpoints plus the shared error envelope.
/// </summary>
[TestClass]
public sealed class AuthHttpContractTests
{
    private const string ProblemJson = "application/problem+json";

    private AuthTestFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Initialize()
    {
        _factory = new AuthTestFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task UnknownUsernameAndWrongPassword_ReturnIndistinguishable401WithoutSideEffects()
    {
        using var unknown = await LoginAsync("no-such-user", AuthTestFactory.DemoPassword);
        var unknownProblem = await ReadProblemAsync(unknown);
        using var wrongPassword = await LoginAsync(AuthTestFactory.DemoUsername, "not-the-password");
        var wrongPasswordProblem = await ReadProblemAsync(wrongPassword);

        AssertProblem(unknownProblem, HttpStatusCode.Unauthorized, "UNAUTHENTICATED", "/auth/login");
        AssertProblem(wrongPasswordProblem, HttpStatusCode.Unauthorized, "UNAUTHENTICATED", "/auth/login");

        Assert.AreEqual(unknownProblem.Title, wrongPasswordProblem.Title);
        Assert.AreEqual(unknownProblem.Code, wrongPasswordProblem.Code);
        Assert.AreEqual(unknownProblem.Message, wrongPasswordProblem.Message);
        Assert.AreEqual(unknownProblem.JsonStatus, wrongPasswordProblem.JsonStatus);

        Assert.AreEqual(0, _factory.FakeSms.SendCount, "A failed login must not send an OTP.");
        Assert.AreEqual(0, ChallengeCount(), "A failed login must not leave a challenge behind.");
    }

    [TestMethod]
    public async Task LoginSuccess_IssuesChallengeSendsOneOtpAndUserInfoReturnsProfile()
    {
        using var login = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        Assert.AreEqual(
            HttpStatusCode.OK,
            login.StatusCode,
            await login.Content.ReadAsStringAsync());

        var loginBody = await login.Content.ReadAsStringAsync();
        using var loginDocument = JsonDocument.Parse(loginBody);
        var challenge = loginDocument.RootElement;

        var challengeId = challenge.GetProperty("challenge_id").GetString();
        Assert.IsTrue(Guid.TryParse(challengeId, out _), $"challenge_id was '{challengeId}'.");
        Assert.AreEqual(300, challenge.GetProperty("expires_in").GetInt32());
        Assert.AreEqual(60, challenge.GetProperty("resend_in").GetInt32());
        Assert.AreEqual("no-store", login.Headers.CacheControl?.ToString());

        Assert.AreEqual(1, _factory.FakeSms.SendCount, "Login must send exactly one OTP.");
        var sms = _factory.FakeSms.Messages.Single();
        Assert.AreEqual(AuthTestFactory.DemoPhone, sms.Phone);
        StringAssert.Contains(sms.Message, AuthTestFactory.DemoOtp);

        using var verify = await _client.PostAsJsonAsync(
            $"/auth/challenges/{challengeId}/verify",
            new { code = AuthTestFactory.DemoOtp });
        Assert.AreEqual(
            HttpStatusCode.OK,
            verify.StatusCode,
            await verify.Content.ReadAsStringAsync());

        using var verifyDocument = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        var token = verifyDocument.RootElement.GetProperty("access_token").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        Assert.AreEqual("Bearer", verifyDocument.RootElement.GetProperty("token_type").GetString());
        Assert.AreEqual(1800, verifyDocument.RootElement.GetProperty("expires_in").GetInt32());

        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/userinfo");
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var userInfo = await _client.SendAsync(userInfoRequest);
        Assert.AreEqual(
            HttpStatusCode.OK,
            userInfo.StatusCode,
            await userInfo.Content.ReadAsStringAsync());

        using var userInfoDocument = JsonDocument.Parse(await userInfo.Content.ReadAsStringAsync());
        var profile = userInfoDocument.RootElement;
        Assert.AreEqual(AuthTestFactory.DemoUserId, profile.GetProperty("id").GetString());
        Assert.AreEqual("Alex Morgan", profile.GetProperty("name").GetString());
        Assert.AreEqual(AuthTestFactory.DemoPhone, profile.GetProperty("phone").GetString());
        Assert.AreEqual(AuthTestFactory.OrgId, profile.GetProperty("org").GetString());
        Assert.AreEqual("no-store", userInfo.Headers.CacheControl?.ToString());

        Assert.AreEqual(0, ChallengeCount(), "Successful verification must consume the challenge.");
    }

    [TestMethod]
    public async Task ThreeWrongCodes_DestroyTheChallenge_AndTheCorrectCodeCannotResurrectIt()
    {
        var challengeId = await LoginAndGetChallengeIdAsync();

        using var first = await VerifyAsync(challengeId, "000000");
        AssertProblem(await ReadProblemAsync(first), HttpStatusCode.Unauthorized, "UNAUTHENTICATED", $"/auth/challenges/{challengeId}/verify");

        using var second = await VerifyAsync(challengeId, "000000");
        AssertProblem(await ReadProblemAsync(second), HttpStatusCode.Unauthorized, "UNAUTHENTICATED", $"/auth/challenges/{challengeId}/verify");

        using var third = await VerifyAsync(challengeId, "000000");
        AssertProblem(await ReadProblemAsync(third), HttpStatusCode.TooManyRequests, "RATE_LIMITED", $"/auth/challenges/{challengeId}/verify");

        using var correctAfterLockout = await VerifyAsync(challengeId, AuthTestFactory.DemoOtp);
        AssertProblem(
            await ReadProblemAsync(correctAfterLockout),
            HttpStatusCode.Unauthorized,
            "UNAUTHENTICATED",
            $"/auth/challenges/{challengeId}/verify");
        Assert.AreEqual(1, _factory.FakeSms.SendCount, "No verification attempt must send another OTP.");
    }

    [TestMethod]
    public async Task Resend_IsRateLimitedDuringCooldown_AndSucceedsAfterIt()
    {
        var challengeId = await LoginAndGetChallengeIdAsync();
        Assert.AreEqual(1, _factory.FakeSms.SendCount);

        using var immediate = await _client.PostAsync(
            $"/auth/challenges/{challengeId}/resend",
            content: null);
        AssertProblem(
            await ReadProblemAsync(immediate),
            HttpStatusCode.TooManyRequests,
            "RATE_LIMITED",
            $"/auth/challenges/{challengeId}/resend");

        var retryAfter = immediate.Headers.GetValues("Retry-After").Single();
        Assert.IsTrue(
            int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var retryAfterSeconds),
            $"Retry-After must be a numeric number of seconds, was '{retryAfter}'.");
        Assert.IsTrue(retryAfterSeconds > 0);
        Assert.AreEqual(1, _factory.FakeSms.SendCount, "A rejected resend must not send an OTP.");

        _factory.Advance(TimeSpan.FromSeconds(60));

        using var afterCooldown = await _client.PostAsync(
            $"/auth/challenges/{challengeId}/resend",
            content: null);
        Assert.AreEqual(
            HttpStatusCode.OK,
            afterCooldown.StatusCode,
            await afterCooldown.Content.ReadAsStringAsync());

        using var resendDocument = JsonDocument.Parse(await afterCooldown.Content.ReadAsStringAsync());
        var resent = resendDocument.RootElement;
        Assert.AreEqual(challengeId, resent.GetProperty("challenge_id").GetString());
        Assert.AreEqual(240, resent.GetProperty("expires_in").GetInt32());
        Assert.AreEqual(60, resent.GetProperty("resend_in").GetInt32());

        Assert.AreEqual(2, _factory.FakeSms.SendCount, "A successful resend must send a new OTP.");
        StringAssert.Contains(_factory.FakeSms.Messages[^1].Message, AuthTestFactory.DemoOtp);
    }

    [TestMethod]
    public async Task SmsProviderFailure_Returns503AndDoesNotCreateAnOrphanChallenge()
    {
        _factory.FakeSms.Failure = new SmsDeliveryException("provider unavailable");

        using var failedLogin = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        AssertProblem(
            await ReadProblemAsync(failedLogin),
            HttpStatusCode.ServiceUnavailable,
            "UPSTREAM_UNAVAILABLE",
            "/auth/login");

        Assert.AreEqual(
            TimeSpan.FromSeconds(5),
            failedLogin.Headers.RetryAfter?.Delta,
            "A 503 must tell the caller how long to wait.");
        Assert.AreEqual(0, _factory.FakeSms.SendCount);
        Assert.AreEqual(0, ChallengeCount(), "A failed SMS send must not create a challenge.");

        // The host is still healthy once the gateway recovers.
        _factory.FakeSms.Failure = null;
        using var recovered = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        Assert.AreEqual(1, ChallengeCount());
    }

    [TestMethod]
    public async Task UnsupportedContentType_Returns415Envelope()
    {
        using var content = new StringContent(
            "{\"username\":\"demo\",\"password\":\"Demo_Passw0rd!\"}",
            Encoding.UTF8,
            "text/plain");

        using var response = await _client.PostAsync("/auth/login", content);

        AssertProblem(
            await ReadProblemAsync(response),
            HttpStatusCode.UnsupportedMediaType,
            "UNSUPPORTED_MEDIA_TYPE",
            "/auth/login");
    }

    [TestMethod]
    public async Task MalformedJson_UnknownRoute_AndWrongMethod_UseTheSharedEnvelope()
    {
        using var malformedContent = new StringContent(
            "{\"username\":",
            Encoding.UTF8,
            "application/json");
        using var malformed = await _client.PostAsync("/auth/login", malformedContent);
        AssertProblem(
            await ReadProblemAsync(malformed),
            HttpStatusCode.BadRequest,
            "VALIDATION_ERROR",
            "/auth/login");

        using var unknownRoute = await _client.GetAsync("/auth/no-such-route");
        AssertProblem(
            await ReadProblemAsync(unknownRoute),
            HttpStatusCode.NotFound,
            "NOT_FOUND",
            "/auth/no-such-route");

        using var wrongMethod = await _client.GetAsync("/auth/login");
        AssertProblem(
            await ReadProblemAsync(wrongMethod),
            HttpStatusCode.MethodNotAllowed,
            "METHOD_NOT_ALLOWED",
            "/auth/login");
    }

    [TestMethod]
    public async Task SecurityHeaders_NosniffIsPresentOnHealthAndAuthErrors()
    {
        using var health = await _client.GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        AssertNosniff(health, "/healthz");

        using var rejected = await LoginAsync("no-such-user", "not-the-password");
        Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertNosniff(rejected, "401 /auth/login");

        using var unknown = await _client.GetAsync("/auth/no-such-route");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertNosniff(unknown, "404 auth route");

        // The existing no-store contract for /auth responses is unchanged.
        Assert.AreEqual("no-store", rejected.Headers.CacheControl?.ToString());
    }

    private async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await _client.PostAsJsonAsync("/auth/login", new { username, password });

    private async Task<string> LoginAndGetChallengeIdAsync()
    {
        using var login = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        Assert.AreEqual(
            HttpStatusCode.OK,
            login.StatusCode,
            await login.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("challenge_id").GetString()!;
    }

    private async Task<HttpResponseMessage> VerifyAsync(string challengeId, string code) =>
        await _client.PostAsJsonAsync($"/auth/challenges/{challengeId}/verify", new { code });

    private static async Task<ProblemEnvelope> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var error = root.GetProperty("error");

        return new ProblemEnvelope(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            root.GetProperty("status").GetInt32(),
            root.GetProperty("title").GetString(),
            root.GetProperty("instance").GetString(),
            root.GetProperty("traceId").GetString(),
            error.GetProperty("code").GetString(),
            error.GetProperty("message").GetString(),
            body);
    }

    private static void AssertProblem(
        ProblemEnvelope problem,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedPath)
    {
        Assert.AreEqual(expectedStatus, problem.Status, problem.Body);
        Assert.AreEqual(ProblemJson, problem.MediaType, problem.Body);
        Assert.AreEqual((int)expectedStatus, problem.JsonStatus, problem.Body);
        Assert.AreEqual(expectedCode, problem.Code, problem.Body);
        Assert.AreEqual(expectedCode, problem.Title, problem.Body);
        Assert.AreEqual(expectedPath, problem.Instance, problem.Body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.TraceId), problem.Body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.Message), problem.Body);
    }

    private static void AssertNosniff(HttpResponseMessage response, string scenario)
    {
        Assert.IsTrue(
            response.Headers.TryGetValues("X-Content-Type-Options", out var values),
            $"{scenario}: the response must carry X-Content-Type-Options.");
        Assert.AreEqual(
            "nosniff",
            values!.Single(),
            $"{scenario}: the response must disable MIME sniffing.");
    }

    /// <summary>
    /// Reads the private challenge dictionary to prove a failed login created no
    /// challenge. The store exposes no public count and this test must not add
    /// production surface just for observability.
    /// </summary>
    private int ChallengeCount()
    {
        var store = _factory.Services.GetRequiredService<ChallengeStore>();
        var field = typeof(ChallengeStore).GetField(
            "_challenges",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);

        var value = field.GetValue(store);
        Assert.IsInstanceOfType<System.Collections.ICollection>(value);
        return ((System.Collections.ICollection)value!).Count;
    }

    private sealed record ProblemEnvelope(
        HttpStatusCode Status,
        string? MediaType,
        int JsonStatus,
        string? Title,
        string? Instance,
        string? TraceId,
        string? Code,
        string? Message,
        string Body);
}
