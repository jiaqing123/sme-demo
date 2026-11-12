using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SmeDemo.Api.Configuration;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Errors;
using SmeDemo.Application.Features.Transactions;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Runs the real API host with seed disabled, fake storage ports, and a local RSA
/// key instead of a live auth metadata fetch.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "http://auth:8081";
    public const string Audience = "sme-demo-api";
    public const string OrgId = "sme";
    private const string DefaultDatabaseUrl =
        "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Port 1 on loopback has no listener, so the metadata fetch fails at the
    /// transport layer instead of reaching a signed test configuration.
    /// </summary>
    private const string UnreachableMetadataUrl =
        "http://127.0.0.1:1/.well-known/openid-configuration";

    public static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");
    public static readonly Guid OtherUserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000002");

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "test-signing-key" };
    private static readonly JsonWebTokenHandler TokenHandler = new()
    {
        // Match the issuer: no implicit nbf claim, so an expired token fails on
        // exp and maps to TOKEN_EXPIRED.
        SetDefaultTimesOnTokenCreation = false,
    };

    private readonly string _databaseUrl;
    private readonly string _jwtAudience;
    private readonly int? _rateLimitPermit;
    private readonly TimeSpan? _rateLimitWindow;
    private readonly bool _unreachableAuthMetadata;

    public ApiTestFactory(
        string? databaseUrl = null,
        string? jwtAudience = null,
        int? rateLimitPermit = null,
        TimeSpan? rateLimitWindow = null,
        bool unreachableAuthMetadata = false)
    {
        _databaseUrl = databaseUrl ?? DefaultDatabaseUrl;
        _jwtAudience = jwtAudience ?? Audience;
        _rateLimitPermit = rateLimitPermit;
        _rateLimitWindow = rateLimitWindow;
        _unreachableAuthMetadata = unreachableAuthMetadata;
        SeedData();
    }

    /// <summary>
    /// The options this host resolved from its own configuration. It proves the
    /// per-factory settings reach the top-level Program without touching process
    /// environment variables.
    /// </summary>
    public ApiOptions Options => Services.GetRequiredService<ApiOptions>();

    public FakeCardRepository Cards { get; } = new();

    public FakeTransactionStore Transactions { get; } = new();

    public FakeBlobStore Blobs { get; } = new();

    /// <summary>
    /// Deterministic readiness probes. The real probes would reach the test
    /// host's inert SQL Server, Azurite, and auth metadata URL, so
    /// <see cref="ConfigureWebHost"/> replaces them for every factory.
    /// </summary>
    public FakeReadinessProbe DatabaseProbe { get; } = new("database");

    public FakeReadinessProbe BlobStorageProbe { get; } = new("blob-storage");

    public FakeReadinessProbe AuthMetadataProbe { get; } = new("auth-metadata");

    /// <summary>Sets every dependency's readiness in one call.</summary>
    public void SetReadiness(bool ready)
    {
        DatabaseProbe.Ready = ready;
        BlobStorageProbe.Ready = ready;
        AuthMetadataProbe.Ready = ready;
    }

    /// <summary>
    /// Captures every formatted log message (and its template) the API emits, so
    /// the security tests can prove no token, OTP, phone number, or password is
    /// ever written to the log.
    /// </summary>
    public CapturingLoggerProvider Logs { get; } = new();

    public string CreateToken(
        string org = OrgId,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = _jwtAudience,
            IssuedAt = issued.UtcDateTime,
            Expires = (expiresAt ?? issued.AddMinutes(30)).UtcDateTime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = UserId.ToString(),
                ["org"] = org,
                ["name"] = "Alex Morgan",
                ["jti"] = Guid.NewGuid().ToString(),
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        };

        return TokenHandler.CreateToken(descriptor);
    }

    /// <summary>
    /// Builds a token with explicit claim, key, or signature overrides for the
    /// negative authentication matrix. This is deliberately separate from
    /// <see cref="CreateToken"/> so the happy-path helper keeps its exact
    /// contract; pass <paramref name="unsigned"/> to emit an <c>alg=none</c>
    /// token with no signature.
    /// </summary>
    public string CreateTokenWith(
        string? issuer = null,
        string? audience = null,
        string? org = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        SigningCredentials? signingCredentials = null,
        bool unsigned = false)
    {
        var issued = issuedAt ?? DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? _jwtAudience,
            IssuedAt = issued.UtcDateTime,
            Expires = (expiresAt ?? issued.AddMinutes(30)).UtcDateTime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = UserId.ToString(),
                ["org"] = org ?? OrgId,
                ["name"] = "Alex Morgan",
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };

        if (!unsigned)
        {
            descriptor.SigningCredentials = signingCredentials
                ?? new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256);
        }

        return TokenHandler.CreateToken(descriptor);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            logging.AddProvider(Logs);
        });
        // Host settings are per factory and visible to builder.Configuration
        // before the minimal-hosting top-level Program resolves ApiOptions.
        // ConfigureAppConfiguration is installed after that read, and process
        // environment variables would leak between factories.
        builder.UseSetting("DATABASE_URL", _databaseUrl);
        builder.UseSetting("AZURE_STORAGE_CONNECTION_STRING", "UseDevelopmentStorage=true");
        builder.UseSetting("SEED_ENABLED", "false");
        builder.UseSetting(
            "AUTH_METADATA_URL",
            _unreachableAuthMetadata ? UnreachableMetadataUrl : "http://localhost:1/.well-known/openid-configuration");
        builder.UseSetting("AUTH_REQUIRE_HTTPS_METADATA", "false");
        builder.UseSetting("JWT_ISSUER", Issuer);
        builder.UseSetting("JWT_AUDIENCE", _jwtAudience);
        builder.UseSetting("ORG_ID", OrgId);
        builder.UseSetting("PUBLIC_BASE_URL", "http://localhost:8082");
        builder.UseSetting("CORS_ORIGIN", "http://localhost:8080");
        builder.UseSetting("ISSUER_SYNC_ENABLED", "false");

        if (_rateLimitPermit is { } permit)
        {
            builder.UseSetting("API_RATE_LIMIT_PERMIT", permit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_rateLimitWindow is { } window)
        {
            // DurationParser reads a bare number as seconds; use whole seconds
            // to keep the test setting unambiguous.
            builder.UseSetting(
                "API_RATE_LIMIT_WINDOW",
                $"{Math.Max(1, (int)Math.Ceiling(window.TotalSeconds))}s");
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICardRepository>();
            services.RemoveAll<ITransactionStore>();
            services.RemoveAll<IBlobStore>();
            services.RemoveAll<IReadinessProbe>();

            services.AddSingleton<ICardRepository>(Cards);
            services.AddSingleton<ITransactionStore>(Transactions);
            services.AddSingleton<IBlobStore>(Blobs);
            services.AddSingleton<IReadinessProbe>(DatabaseProbe);
            services.AddSingleton<IReadinessProbe>(BlobStorageProbe);
            services.AddSingleton<IReadinessProbe>(AuthMetadataProbe);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                if (_unreachableAuthMetadata)
                {
                    // Leave the production configuration in place: the handler
                    // must attempt the real metadata fetch and fail at transport.
                    return;
                }

                options.MetadataAddress = string.Empty;
                options.ConfigurationManager = null;
                options.RequireHttpsMetadata = false;
                options.MapInboundClaims = false;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(SigningKey);
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = _jwtAudience;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.IssuerSigningKeys = [SigningKey];
            });
        });
    }

    private void SeedData()
    {
        var first = Card.Create(
            Guid.Parse("c1000000-0000-4000-8000-000000000001"),
            UserId,
            OrgId,
            "visa",
            "4242",
            9,
            2028,
            "AUD",
            0,
            "visa-4242.svg");
        var second = Card.Create(
            Guid.Parse("c1000000-0000-4000-8000-000000000002"),
            UserId,
            OrgId,
            "mastercard",
            "5454",
            3,
            2027,
            "AUD",
            1,
            "mastercard-5454.svg");
        var third = Card.Create(
            Guid.Parse("c1000000-0000-4000-8000-000000000003"),
            UserId,
            OrgId,
            "visa",
            "1881",
            11,
            2026,
            "AUD",
            2,
            "visa-1881.svg");
        var other = Card.Create(
            Guid.Parse("c1000000-0000-4000-8000-000000000099"),
            OtherUserId,
            OrgId,
            "visa",
            "9999",
            1,
            2029,
            "AUD",
            0,
            "other.svg");

        Cards.Cards.AddRange([third, first, second, other]);

        for (var index = 1; index <= 8; index++)
        {
            Transactions.Records.Add(CreateTransaction(first.Id, index));
        }

        Blobs.Blobs["visa-4242.svg"] = (Encoding.UTF8.GetBytes("<svg>4242</svg>"), "image/svg+xml");
        Blobs.Blobs["mastercard-5454.svg"] = (Encoding.UTF8.GetBytes("<svg>5454</svg>"), "image/svg+xml");
        Blobs.Blobs["visa-1881.svg"] = (Encoding.UTF8.GetBytes("<svg>1881</svg>"), "image/svg+xml");
    }

    private static TransactionRecord CreateTransaction(Guid cardId, int sequence)
    {
        var occurredAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddHours(-sequence);
        var transactionId = Guid.Parse($"00000000-0000-4000-8000-{sequence:D12}");

        return new TransactionRecord(
            TransactionKeys.CreateRowKey(occurredAt, transactionId),
            cardId,
            transactionId,
            1000 + sequence,
            "AUD",
            "debit",
            "Woolworths",
            "posted",
            occurredAt,
            occurredAt.AddHours(2),
            "{}");
    }
}

public sealed class FakeCardRepository : ICardRepository
{
    public List<Card> Cards { get; } = [];

    /// <summary>
    /// When set, every read throws this plain exception. The status-code matrix
    /// uses it to reach the unhandled-failure (500) path without a real
    /// database fault.
    /// </summary>
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<Card>> GetByUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured();

        return Task.FromResult<IReadOnlyList<Card>>(
            Cards.Where(card => card.UserId == userId).ToArray());
    }

    public Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken)
    {
        ThrowIfConfigured();

        return Task.FromResult(Cards.SingleOrDefault(card => card.Id == cardId));
    }

    private void ThrowIfConfigured()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

public sealed class FakeTransactionStore : ITransactionStore
{
    public List<TransactionRecord> Records { get; } = [];

    /// <summary>Set to simulate an unreachable store after retries.</summary>
    public bool Unavailable { get; set; }

    public Task<TransactionPage> ReadAsync(
        Guid cardId,
        string? afterRowKey,
        int limit,
        CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new StoreUnavailableException("The transaction store is unavailable.");
        }

        var ordered = Records
            .Where(record => record.CardId == cardId)
            .OrderBy(record => record.RowKey, StringComparer.Ordinal)
            .ToArray();

        var remaining = afterRowKey is null
            ? ordered
            : ordered.Where(record => string.CompareOrdinal(record.RowKey, afterRowKey) > 0).ToArray();

        var items = remaining.Take(limit).ToArray();
        var next = remaining.Length > limit ? items[^1].RowKey : null;

        return Task.FromResult(new TransactionPage(items, next));
    }

    public Task UpsertAsync(
        IReadOnlyCollection<TransactionRecord> records,
        CancellationToken cancellationToken)
    {
        Records.Clear();
        Records.AddRange(records);
        return Task.CompletedTask;
    }
}

public sealed class FakeBlobStore : IBlobStore
{
    public Dictionary<string, (byte[] Content, string ContentType)> Blobs { get; } =
        new(StringComparer.Ordinal);

    public Task<BlobContent?> OpenReadAsync(string blobName, CancellationToken cancellationToken) =>
        Task.FromResult(Blobs.TryGetValue(blobName, out var blob)
            ? new BlobContent(new MemoryStream(blob.Content), blob.ContentType)
            : null);

    public Task UploadAsync(
        string blobName,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The API does not upload artwork at run time.");
}

public sealed class FakeReadinessProbe(string name) : IReadinessProbe
{
    public string Name { get; } = name;

    /// <summary>Toggle the simulated dependency state for the next probe call.</summary>
    public bool Ready { get; set; } = true;

    public Task<bool> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);
}

/// <summary>
/// Test-only <see cref="ILoggerProvider"/> that records formatted messages and
/// their <c>{OriginalFormat}</c> templates in memory. The security tests scan
/// this text for secrets; it never leaves the test process.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    /// <summary>All captured messages as one searchable text block.</summary>
    public string Text => string.Join(
        Environment.NewLine,
        _entries.Select(entry =>
            $"{entry.Category} [{entry.Level}] {entry.Message} | template: {entry.Template}"));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(CapturedLogEntry entry) => _entries.Enqueue(entry);

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var template = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.LastOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string
                : null;

            owner.Add(new CapturedLogEntry(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                template ?? string.Empty));
        }
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    string Template);
