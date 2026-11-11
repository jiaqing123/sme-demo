using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmeDemo.Auth.Challenges;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Contracts;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Services;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// Covers the flow-level contract around SMS delivery: demo behaviour is
/// unchanged when the provider works, and a gateway failure becomes a readable
/// 503 instead of an unhandled exception. The fake provider is the seam.
/// </summary>
[TestClass]
public sealed class AuthFlowServiceSmsFailureTests
{
    private static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string DemoPassword = "Demo_Passw0rd!";
    private const string DemoPhone = "+61400000000";

    private string _keysPath = null!;
    private SigningKeyStore _keys = null!;
    private AuthDbContext _db = null!;
    private AuthOptions _options = null!;
    private ManualTimeProvider _clock = null!;
    private ChallengeStore _challenges = null!;
    private FakeSmsProvider _sms = null!;
    private AuthFlowService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _keysPath = Path.Combine(Path.GetTempPath(), $"sme-auth-sms-tests-{Guid.NewGuid():N}");
        _keys = new SigningKeyStore(_keysPath);
        _clock = new ManualTimeProvider(Start);
        _challenges = new ChallengeStore(_clock);
        _sms = new FakeSmsProvider();

        _options = new AuthOptions(
            DatabaseUrl: "Server=unused;Database=sme_auth;User Id=unused;Password=unused;TrustServerCertificate=True",
            OrgId: "sme",
            DemoMode: true,
            DemoUsername: "demo",
            DemoPassword: DemoPassword,
            DemoPhone: DemoPhone,
            DemoOtp: "123456",
            DemoOtpWasConfigured: true,
            JwtIssuer: "http://auth:8081",
            JwtAudience: "sme-demo-api",
            JwtTtl: TimeSpan.FromMinutes(30),
            CorsOrigin: "http://localhost:8080",
            KeysPath: _keysPath,
            SmsProviderUrl: "http://sms-mock:8083");

        var dbOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"sme-auth-sms-{Guid.NewGuid():N}")
            .Options;

        _db = new AuthDbContext(dbOptions);
        var hasher = new PasswordHasher<AuthUser>();
        var user = AuthUser.Create(UserId, "sme", "demo", "Alex Morgan", DemoPhone, Start.UtcDateTime);
        user.SetPasswordHash(hasher.HashPassword(user, DemoPassword));
        _db.Users.Add(user);
        _db.SaveChanges();

        _service = new AuthFlowService(
            _db,
            _challenges,
            hasher,
            new JwtTokenIssuer(_keys, _options, _clock),
            _sms,
            _options,
            NullLogger<AuthFlowService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _keys.Dispose();
        if (Directory.Exists(_keysPath))
        {
            Directory.Delete(_keysPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DemoLogin_SendsFixedOtpAndVerifyStillReturnsAToken()
    {
        var login = await _service.LoginAsync(
            new LoginRequest("demo", DemoPassword),
            CancellationToken.None);

        Assert.IsTrue(login.Succeeded, login.Problem?.Message);
        Assert.AreEqual(1, _sms.Messages.Count);
        Assert.AreEqual(DemoPhone, _sms.Messages[0].Phone);
        StringAssert.Contains(_sms.Messages[0].Message, "123456");
        StringAssert.Contains(_sms.Messages[0].Message, "Your SME Demo verification code is");

        var challengeId = login.Value!.ChallengeId;
        var verify = await _service.VerifyAsync(
            challengeId,
            new VerifyRequest("123456"),
            CancellationToken.None);

        Assert.IsTrue(verify.Succeeded, verify.Problem?.Message);
        Assert.IsFalse(string.IsNullOrWhiteSpace(verify.Value!.AccessToken));
        Assert.AreEqual("Bearer", verify.Value.TokenType);
    }

    [TestMethod]
    public async Task Login_WhenSmsProviderFails_Returns503UpstreamUnavailable()
    {
        _sms.FailWith = new SmsDeliveryException("connection refused");

        var login = await _service.LoginAsync(
            new LoginRequest("demo", DemoPassword),
            CancellationToken.None);

        Assert.IsFalse(login.Succeeded);
        Assert.IsNull(login.Value);
        Assert.IsNotNull(login.Problem);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, login.Problem.StatusCode);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, login.Problem.Code);
        Assert.AreEqual("The SMS provider is unavailable. Try again shortly.", login.Problem.Message);
        Assert.AreEqual(0, _sms.Messages.Count);
    }

    [TestMethod]
    public async Task Resend_WhenSmsProviderFails_Returns503UpstreamUnavailable()
    {
        var login = await _service.LoginAsync(
            new LoginRequest("demo", DemoPassword),
            CancellationToken.None);
        Assert.IsTrue(login.Succeeded);

        _clock.Advance(ChallengeStore.ResendCooldown);
        _sms.FailWith = new SmsDeliveryException("timeout");

        var resend = await _service.ResendAsync(login.Value!.ChallengeId, CancellationToken.None);

        Assert.IsFalse(resend.Succeeded);
        Assert.IsNull(resend.Value);
        Assert.IsNotNull(resend.Problem);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, resend.Problem.StatusCode);
        Assert.AreEqual(ErrorCodes.UpstreamUnavailable, resend.Problem.Code);
        Assert.AreEqual("The SMS provider is unavailable. Try again shortly.", resend.Problem.Message);
        Assert.AreEqual(1, _sms.Messages.Count);
    }

    private sealed class FakeSmsProvider : ISmsProvider
    {
        public List<(string Phone, string Message)> Messages { get; } = [];

        public Exception? FailWith { get; set; }

        public Task<string> Send(string phoneNumber, string message, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Messages.Add((phoneNumber, message));
            return Task.FromResult("sms-test");
        }
    }
}
