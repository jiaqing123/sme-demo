using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Auth.Challenges;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// HTTP-level abuse tests for the login budgets in threat-model §5.1/§5.2. Each
/// test uses a fresh factory with deliberately tiny limits so the burst is quick
/// and deterministic; the per-instance settings keep factories independent.
/// </summary>
[TestClass]
public sealed class AuthRateLimitTests
{
    private const string ProblemJson = "application/problem+json";

    private AuthTestFactory _factory = null!;
    private HttpClient _client = null!;

    [TestCleanup]
    public void Cleanup()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task LoginBurstFromOneIp_DifferentUsernames_Returns429WithoutSideEffects()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_LOGIN_IP"] = "2",
            ["AUTH_RATE_LIMIT_LOGIN_IP_WINDOW"] = "1m",
            // Keep the account budget out of this test: every request below has
            // a different username anyway.
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT"] = "100",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW"] = "5m",
        });

        using var first = await LoginAsync("no-such-user-one", "wrong-password");
        Assert.AreEqual(HttpStatusCode.Unauthorized, first.StatusCode, await first.Content.ReadAsStringAsync());

        using var second = await LoginAsync("no-such-user-two", "wrong-password");
        Assert.AreEqual(HttpStatusCode.Unauthorized, second.StatusCode, await second.Content.ReadAsStringAsync());

        // The third request is well-formed, so without the IP limiter it would
        // reach the database and, for the demo account, send an SMS.
        using var rejected = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);

        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            rejected.StatusCode,
            await rejected.Content.ReadAsStringAsync());

        var problem = await ReadProblemAsync(rejected);
        AssertProblem(problem, "/auth/login");
        AssertNumericRetryAfter(rejected);

        Assert.AreEqual(0, _factory.FakeSms.SendCount, "A rejected login must not send an OTP.");
        Assert.AreEqual(0, ChallengeCount(), "A rejected login must not create a challenge.");
    }

    [TestMethod]
    public async Task LoginBurstForOneAccount_WithIpBudgetRaised_Returns429()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_LOGIN_IP"] = "100",
            ["AUTH_RATE_LIMIT_LOGIN_IP_WINDOW"] = "1m",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT"] = "2",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW"] = "5m",
        });

        using var first = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());

        // Trimmed and upper-cased variants normalize to the same "demo" key, so
        // this is the second and last permitted attempt for the account.
        using var second = await LoginAsync($" {AuthTestFactory.DemoUsername} ", AuthTestFactory.DemoPassword);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
        Assert.AreEqual(2, _factory.FakeSms.SendCount);
        Assert.AreEqual(2, ChallengeCount());

        using var rejected = await LoginAsync(
            AuthTestFactory.DemoUsername.ToUpperInvariant(),
            AuthTestFactory.DemoPassword);

        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            rejected.StatusCode,
            await rejected.Content.ReadAsStringAsync());

        var problem = await ReadProblemAsync(rejected);
        AssertProblem(problem, "/auth/login");
        AssertNumericRetryAfter(rejected);

        Assert.AreEqual(2, _factory.FakeSms.SendCount, "A rejected login must not send an OTP.");
        Assert.AreEqual(2, ChallengeCount(), "A rejected login must not create a challenge.");
    }

    [TestMethod]
    public async Task LoginBelowTheLimit_StillIssuesChallengeAndSendsOneOtp()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_LOGIN_IP"] = "2",
            ["AUTH_RATE_LIMIT_LOGIN_ACCOUNT"] = "2",
        });

        using var response = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, _factory.FakeSms.SendCount);
        Assert.AreEqual(1, ChallengeCount());
    }

    [TestMethod]
    public async Task VerifyBurstFromOneIp_RejectsTheCorrectCodeAfterTheBudgetIsGone()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_VERIFY_IP"] = "2",
            ["AUTH_RATE_LIMIT_VERIFY_IP_WINDOW"] = "1m",
        });

        var challengeId = await LoginAndGetChallengeIdAsync();

        using var first = await VerifyAsync(challengeId, "000000");
        Assert.AreEqual(HttpStatusCode.Unauthorized, first.StatusCode);

        using var second = await VerifyAsync(challengeId, "000000");
        Assert.AreEqual(HttpStatusCode.Unauthorized, second.StatusCode);

        // The challenge still has one attempt left, but the IP budget is gone;
        // a rejected request must be the limiter, not the challenge store.
        using var third = await VerifyAsync(challengeId, AuthTestFactory.DemoOtp);
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            third.StatusCode,
            await third.Content.ReadAsStringAsync());

        var problem = await ReadProblemAsync(third);
        Assert.AreEqual("RATE_LIMITED", problem.Code);
        AssertNumericRetryAfter(third);
    }

    [TestMethod]
    public async Task ResendBurstFromOneIp_IsRejectedEvenAfterTheChallengeCooldown()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_RATE_LIMIT_RESEND_IP"] = "1",
            // Longer than the challenge cooldown so advancing 60s cannot reset
            // the limiter window and make this test time-dependent.
            ["AUTH_RATE_LIMIT_RESEND_IP_WINDOW"] = "5m",
        });

        var challengeId = await LoginAndGetChallengeIdAsync();

        // The first resend is rejected by the challenge cooldown but still
        // consumes the single IP permit.
        using var duringCooldown = await _client.PostAsync(
            $"/auth/challenges/{challengeId}/resend",
            content: null);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, duringCooldown.StatusCode);

        _factory.Advance(TimeSpan.FromSeconds(60));

        // Without the IP limiter this would now succeed and send an OTP.
        using var afterCooldown = await _client.PostAsync(
            $"/auth/challenges/{challengeId}/resend",
            content: null);
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            afterCooldown.StatusCode,
            await afterCooldown.Content.ReadAsStringAsync());

        var problem = await ReadProblemAsync(afterCooldown);
        Assert.AreEqual("RATE_LIMITED", problem.Code);
        Assert.AreEqual(1, _factory.FakeSms.SendCount, "A rate-limited resend must not send an OTP.");
    }

    [TestMethod]
    public async Task ForwardedFor_PartitionsPerClient_WhenTrustIsEnabled()
    {
        CreateHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_TRUST_FORWARDED_HEADERS"] = "true",
            ["AUTH_RATE_LIMIT_LOGIN_IP"] = "1",
            ["AUTH_RATE_LIMIT_LOGIN_IP_WINDOW"] = "1m",
        });

        using var first = await LoginWithForwardedForAsync("203.0.113.10");
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());

        using var second = await LoginWithForwardedForAsync("203.0.113.11");
        Assert.AreEqual(
            HttpStatusCode.OK,
            second.StatusCode,
            "A different forwarded client address must get its own budget.");

        using var repeated = await LoginWithForwardedForAsync("203.0.113.10");
        Assert.AreEqual(
            HttpStatusCode.TooManyRequests,
            repeated.StatusCode,
            await repeated.Content.ReadAsStringAsync());
    }

    private void CreateHost(IReadOnlyDictionary<string, string> settings)
    {
        _factory = new AuthTestFactory(settings);
        _client = _factory.CreateClient();
    }

    private async Task<string> LoginAndGetChallengeIdAsync()
    {
        using var login = await LoginAsync(AuthTestFactory.DemoUsername, AuthTestFactory.DemoPassword);
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("challenge_id").GetString()!;
    }

    private async Task<HttpResponseMessage> VerifyAsync(string challengeId, string code) =>
        await _client.PostAsJsonAsync($"/auth/challenges/{challengeId}/verify", new { code });

    private async Task<HttpResponseMessage> LoginWithForwardedForAsync(string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(new
            {
                username = AuthTestFactory.DemoUsername,
                password = AuthTestFactory.DemoPassword,
            }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await _client.PostAsJsonAsync("/auth/login", new { username, password });

    private static void AssertNumericRetryAfter(HttpResponseMessage response)
    {
        Assert.IsTrue(
            response.Headers.TryGetValues("Retry-After", out var values),
            "A 429 must carry a Retry-After header.");

        var raw = values.Single();
        Assert.IsTrue(
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds),
            $"Retry-After must be numeric seconds, was '{raw}'.");
        Assert.IsTrue(seconds > 0, "Retry-After must be positive.");
    }

    private static void AssertProblem(ProblemEnvelope problem, string expectedPath)
    {
        Assert.AreEqual(HttpStatusCode.TooManyRequests, problem.Status, problem.Body);
        Assert.AreEqual(ProblemJson, problem.MediaType, problem.Body);
        Assert.AreEqual(429, problem.JsonStatus, problem.Body);
        Assert.AreEqual("RATE_LIMITED", problem.Code, problem.Body);
        Assert.AreEqual("RATE_LIMITED", problem.Title, problem.Body);
        Assert.AreEqual(expectedPath, problem.Instance, problem.Body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.TraceId), problem.Body);
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.Message), problem.Body);
    }

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

    /// <summary>
    /// Reads the private challenge dictionary to prove a rejected login created
    /// no challenge. The store exposes no public count and this test must not add
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
