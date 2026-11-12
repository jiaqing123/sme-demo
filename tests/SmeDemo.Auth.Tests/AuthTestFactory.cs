using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// Boots the real auth host for HTTP-level tests. The SQL Server database is
/// replaced with a per-factory in-memory database, the SMS gateway with a
/// recording fake, and the system clock with a manual one. All host settings are
/// supplied through <see cref="IWebHostBuilder.UseSetting(string, string)"/> so
/// two factories can run with independent configuration without mutating process
/// environment variables.
/// </summary>
public sealed class AuthTestFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "http://auth:8081";
    public const string Audience = "sme-demo-api";
    public const string OrgId = "sme";
    public const string DemoUsername = "demo";
    public const string DemoPassword = "Demo_Passw0rd!";
    public const string DemoPhone = "+61400000000";
    public const string DemoOtp = "123456";
    public const string DemoUserId = "7f3c2a10-0000-4000-8000-000000000001";

    // Satisfies AuthOptions.DATABASE_URL; the real EF registration is replaced
    // with the in-memory provider below, so this connection is never opened.
    private const string DatabaseUrl =
        "Server=unused;Database=sme_auth;User Id=unused;Password=unused;TrustServerCertificate=True";

    private readonly string _databaseName = $"sme-auth-http-{Guid.NewGuid():N}";
    private readonly string _keysPath;
    private readonly ManualTimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string>? _settings;

    /// <param name="settings">
    /// Optional per-instance host settings applied after the defaults (for
    /// example small <c>AUTH_RATE_LIMIT_*</c> budgets). They use
    /// <see cref="IWebHostBuilder.UseSetting(string, string)"/>, so factories
    /// stay independent and process environment variables are never mutated.
    /// </param>
    public AuthTestFactory(IReadOnlyDictionary<string, string>? settings = null)
    {
        _settings = settings;
        _keysPath = Path.Combine(Path.GetTempPath(), $"sme-auth-http-keys-{Guid.NewGuid():N}");

        // Start at the real instant: UserInfoService validates token lifetime
        // against the system clock, so a token minted near this manual time must
        // still be valid. Challenge TTL and resend cooldown use this clock.
        _clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    }

    public TimeProvider Clock => _clock;

    public RecordingSmsProvider FakeSms { get; } = new();

    public string KeysPath => _keysPath;

    /// <summary>The options this host resolved from its own per-factory settings.</summary>
    public AuthOptions Options => Services.GetRequiredService<AuthOptions>();

    public void Advance(TimeSpan amount) => _clock.Advance(amount);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Host settings are per factory and visible to builder.Configuration
        // before the minimal-hosting top-level Program resolves AuthOptions.
        // ConfigureAppConfiguration would be installed too late, and process
        // environment variables would leak between factories.
        builder.UseSetting("DATABASE_URL", DatabaseUrl);
        builder.UseSetting("DEMO_MODE", "true");
        builder.UseSetting("DEMO_USERNAME", DemoUsername);
        builder.UseSetting("DEMO_PASSWORD", DemoPassword);
        builder.UseSetting("DEMO_PHONE", DemoPhone);
        builder.UseSetting("DEMO_OTP", DemoOtp);
        builder.UseSetting("ORG_ID", OrgId);
        builder.UseSetting("JWT_ISSUER", Issuer);
        builder.UseSetting("JWT_AUDIENCE", Audience);
        builder.UseSetting("JWT_TTL", "30m");
        builder.UseSetting("CORS_ORIGIN", "http://localhost:8080");
        builder.UseSetting("KEYS_PATH", _keysPath);

        if (_settings is not null)
        {
            foreach (var setting in _settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        builder.ConfigureTestServices(services =>
        {
            // Drop the SQL Server DbContext registration completely, including
            // the options configuration action, then register the in-memory
            // provider. Leaving the old IDbContextOptionsConfiguration in place
            // would merge UseSqlServer into the test options.
            services.RemoveAll<AuthDbContext>();
            services.RemoveAll<DbContextOptions<AuthDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
            services.AddDbContext<AuthDbContext>(db => db.UseInMemoryDatabase(_databaseName));

            // ChallengeStore and JwtTokenIssuer both take TimeProvider. Replacing
            // the registration makes the manual clock authoritative for the host.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);

            services.RemoveAll<ISmsProvider>();
            services.AddSingleton<ISmsProvider>(FakeSms);
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                DeleteKeysDirectory();
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            DeleteKeysDirectory();
        }
    }

    private void DeleteKeysDirectory()
    {
        if (Directory.Exists(_keysPath))
        {
            Directory.Delete(_keysPath, recursive: true);
        }
    }
}

public sealed record SentSms(string Phone, string Message);

/// <summary>
/// Test-only SMS seam. It records what the host would have sent and can be told
/// to fail so the 503 path can be exercised.
/// </summary>
public sealed class RecordingSmsProvider : ISmsProvider
{
    private readonly object _sync = new();
    private readonly List<SentSms> _messages = [];

    public IReadOnlyList<SentSms> Messages
    {
        get
        {
            lock (_sync)
            {
                return _messages.ToArray();
            }
        }
    }

    public int SendCount => Messages.Count;

    public SmsDeliveryException? Failure { get; set; }

    /// <summary>
    /// When set, <see cref="Send"/> throws this plain exception instead of a
    /// <see cref="SmsDeliveryException"/>. The status-code matrix uses it to
    /// reach the unhandled-failure (500) path.
    /// </summary>
    public Exception? ThrowOnSend { get; set; }

    public Task<string> Send(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowOnSend is { } throwOnSend)
        {
            throw throwOnSend;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        lock (_sync)
        {
            _messages.Add(new SentSms(phoneNumber, message));
        }

        return Task.FromResult("sms-test");
    }
}
