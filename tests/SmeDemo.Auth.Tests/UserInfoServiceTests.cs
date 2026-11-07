using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Contracts;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Services;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// Covers <c>GET /auth/userinfo</c>'s service layer: the endpoint reads the
/// profile from the database behind a token it verifies itself, so the tests
/// pin both halves of that contract.
/// </summary>
[TestClass]
public sealed class UserInfoServiceTests
{
    private static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");
    private const string Issuer = "http://auth:8081";
    private const string Audience = "sme-demo-api";

    private static readonly JsonWebTokenHandler Handler = new()
    {
        SetDefaultTimesOnTokenCreation = false,
    };

    private string _keysPath = null!;
    private SigningKeyStore _keys = null!;
    private AuthDbContext _db = null!;
    private AuthOptions _options = null!;
    private UserInfoService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _keysPath = Path.Combine(Path.GetTempPath(), $"sme-auth-tests-{Guid.NewGuid():N}");
        _keys = new SigningKeyStore(_keysPath);

        _options = new AuthOptions(
            DatabaseUrl: "Server=unused;Database=sme_auth;User Id=unused;Password=unused;TrustServerCertificate=True",
            OrgId: "sme",
            DemoMode: true,
            DemoUsername: "demo",
            DemoPassword: "Demo_Passw0rd!",
            DemoPhone: "+61400000000",
            DemoOtp: "123456",
            DemoOtpWasConfigured: false,
            JwtIssuer: Issuer,
            JwtAudience: Audience,
            JwtTtl: TimeSpan.FromMinutes(30),
            CorsOrigin: "http://localhost:8080",
            KeysPath: _keysPath,
            SmsProviderUrl: null);

        var dbOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase($"sme-auth-{Guid.NewGuid():N}")
            .Options;

        _db = new AuthDbContext(dbOptions);
        var user = AuthUser.Create(UserId, "sme", "demo", "Alex Morgan", "+61400000000", DateTime.UtcNow);
        user.SetPasswordHash("not-used-by-this-test");
        _db.Users.Add(user);
        _db.SaveChanges();

        _service = new UserInfoService(_db, _keys, _options);
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
    public async Task ValidToken_ReturnsProfileFromDatabase()
    {
        var result = await _service.GetAsync($"Bearer {CreateToken(UserId)}", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        var profile = result.Value!;
        Assert.AreEqual(UserId.ToString(), profile.Id);
        Assert.AreEqual("Alex Morgan", profile.Name);
        Assert.AreEqual("+61400000000", profile.Phone);
        Assert.AreEqual("sme", profile.Org);
    }

    [TestMethod]
    public async Task MissingHeader_ReturnsUnauthenticated()
    {
        var result = await _service.GetAsync(null, CancellationToken.None);
        AssertFailure(result, "UNAUTHENTICATED", 401);
    }

    [TestMethod]
    public async Task NonBearerHeader_ReturnsUnauthenticated()
    {
        var result = await _service.GetAsync($"Basic {CreateToken(UserId)}", CancellationToken.None);
        AssertFailure(result, "UNAUTHENTICATED", 401);
    }

    [TestMethod]
    public async Task ExpiredToken_ReturnsTokenExpired()
    {
        var issuedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var result = await _service.GetAsync(
            $"Bearer {CreateToken(UserId, expiresAt: issuedAt.AddMinutes(30))}",
            CancellationToken.None);

        AssertFailure(result, "TOKEN_EXPIRED", 401);
    }

    [TestMethod]
    public async Task TokenForUnknownUser_ReturnsInvalidToken()
    {
        var result = await _service.GetAsync(
            $"Bearer {CreateToken(Guid.NewGuid())}",
            CancellationToken.None);

        AssertFailure(result, "INVALID_TOKEN", 401);
    }

    [TestMethod]
    public async Task TokenWithWrongAudience_ReturnsInvalidToken()
    {
        var result = await _service.GetAsync(
            $"Bearer {CreateToken(UserId, audience: "another-api")}",
            CancellationToken.None);

        AssertFailure(result, "INVALID_TOKEN", 401);
    }

    [TestMethod]
    public async Task TokenSignedByPreviousKey_IsAcceptedDuringRotation()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"sme-auth-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysPath);

        try
        {
            using var previousRsa = RSA.Create(2048);
            var previousKeyId = Base64UrlEncoder.Encode(
                SHA256.HashData(previousRsa.ExportSubjectPublicKeyInfo()));
            var storedKey = new
            {
                Kid = previousKeyId,
                PrivateKeyPkcs8 = Convert.ToBase64String(previousRsa.ExportPkcs8PrivateKey()),
            };
            File.WriteAllText(
                Path.Combine(keysPath, "signing-key.previous.json"),
                JsonSerializer.Serialize(storedKey));

            using var rotatedKeys = new SigningKeyStore(keysPath);
            Assert.AreEqual(2, rotatedKeys.ValidationKeys.Count);

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                IssuedAt = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(29),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sub"] = UserId.ToString(),
                    ["org"] = "sme",
                },
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(previousRsa) { KeyId = previousKeyId },
                    SecurityAlgorithms.RsaSha256),
            };
            var token = Handler.CreateToken(descriptor);

            var result = await new UserInfoService(_db, rotatedKeys, _options)
                .GetAsync($"Bearer {token}", CancellationToken.None);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(UserId.ToString(), result.Value!.Id);
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    private string CreateToken(
        Guid subject,
        string audience = Audience,
        DateTimeOffset? expiresAt = null)
    {
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = issuedAt.UtcDateTime,
            Expires = (expiresAt ?? issuedAt.AddMinutes(30)).UtcDateTime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = subject.ToString(),
                ["org"] = "sme",
                ["name"] = "Alex Morgan",
                ["jti"] = Guid.NewGuid().ToString(),
            },
            SigningCredentials = new SigningCredentials(_keys.SigningKey, SecurityAlgorithms.RsaSha256),
        };

        return Handler.CreateToken(descriptor);
    }

    private static void AssertFailure(
        OperationResult<UserInfoResponse> result,
        string expectedCode,
        int expectedStatusCode)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Problem);
        Assert.AreEqual(expectedCode, result.Problem.Code);
        Assert.AreEqual(expectedStatusCode, result.Problem.StatusCode);
    }
}
