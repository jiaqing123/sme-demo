using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Security;

namespace SmeDemo.Auth.Tests;

[TestClass]
public sealed class JwtTokenIssuerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private string _keysDirectory = string.Empty;
    private SigningKeyStore _keys = null!;

    [TestInitialize]
    public void Initialize()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), $"sme-auth-keys-{Guid.NewGuid():N}");
        _keys = new SigningKeyStore(_keysDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keys.Dispose();
        if (Directory.Exists(_keysDirectory))
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Issue_HasExactlyTheRequiredClaims()
    {
        var token = IssueToken();

        var claimTypes = token.Claims.Select(claim => claim.Type).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "iss", "aud", "sub", "org", "name", "iat", "exp", "jti" },
            claimTypes,
            string.Join(",", claimTypes));
        Assert.AreEqual("sme", ClaimValue(token, "org"));
        Assert.AreEqual("Alex Morgan", ClaimValue(token, "name"));
        Assert.AreEqual("7f3c2a10-0000-4000-8000-000000000001", ClaimValue(token, "sub"));
        Assert.AreNotEqual(Guid.Empty.ToString(), ClaimValue(token, "jti"));
        Assert.IsFalse(token.Claims.Any(claim => claim.Type == "phone"));
        Assert.IsFalse(token.Claims.Any(claim => claim.Type == "role"));
    }

    [TestMethod]
    public void Issue_UsesRs256WithMatchingKid()
    {
        var token = IssueToken();

        Assert.AreEqual("RS256", token.Alg);
        Assert.AreEqual(_keys.KeyId, token.Kid);
        Assert.AreEqual("http://auth:8081", token.Issuer);
        CollectionAssert.Contains(token.Audiences.ToArray(), "sme-demo-api");
    }

    [TestMethod]
    public void Issue_ExpiresAfterConfiguredTtl()
    {
        var token = IssueToken();

        Assert.AreEqual(Start.UtcDateTime, token.IssuedAt);
        Assert.AreEqual(Start.Add(Ttl).UtcDateTime, token.ValidTo);
    }

    [TestMethod]
    public async Task Issue_ValidatesAgainstPublishedSigningKey()
    {
        var result = Issue();
        var handler = new JsonWebTokenHandler();

        var validation = await handler.ValidateTokenAsync(result.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "http://auth:8081",
            ValidateAudience = true,
            ValidAudience = "sme-demo-api",
            ValidateLifetime = false,
            IssuerSigningKey = _keys.SigningKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.IsTrue(validation.IsValid, validation.Exception?.Message);
    }

    private JsonWebToken IssueToken() => new(Issue().AccessToken);

    private IssuedToken Issue()
    {
        var clock = new ManualTimeProvider(Start);
        var issuer = new JwtTokenIssuer(_keys, CreateOptions(), clock);
        var user = AuthUser.Create(
            Guid.Parse("7f3c2a10-0000-4000-8000-000000000001"),
            "sme",
            "demo",
            "Alex Morgan",
            "+61400000000",
            Start.UtcDateTime);

        return issuer.Issue(user);
    }

    private AuthOptions CreateOptions() => new(
        DatabaseUrl: "Server=localhost;Database=sme_auth;User Id=sa;Password=unused;TrustServerCertificate=True",
        OrgId: "sme",
        DemoMode: true,
        DemoUsername: "demo",
        DemoPassword: "Demo_Passw0rd!",
        DemoPhone: "+61400000000",
        DemoOtp: "123456",
        DemoOtpWasConfigured: true,
        JwtIssuer: "http://auth:8081",
        JwtAudience: "sme-demo-api",
        JwtTtl: Ttl,
        CorsOrigin: "http://localhost:8080",
        KeysPath: _keysDirectory,
        SmsProviderUrl: null,
        RateLimits: AuthRateLimitOptions.Default,
        TrustForwardedHeaders: false);

    private static string ClaimValue(JsonWebToken token, string claimType) =>
        token.Claims.Single(claim => claim.Type == claimType).Value;
}
