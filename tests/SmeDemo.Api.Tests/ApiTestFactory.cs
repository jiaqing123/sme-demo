using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
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

    static ApiTestFactory()
    {
        // ApiOptions reads IConfiguration, which includes environment variables.
        // ConfigureAppConfiguration runs too late for the top-level Program in the
        // minimal-hosting test host, so set the values the host needs here.
        Environment.SetEnvironmentVariable("DATABASE_URL", "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True");
        Environment.SetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING", "UseDevelopmentStorage=true");
        Environment.SetEnvironmentVariable("SEED_ENABLED", "false");
        Environment.SetEnvironmentVariable("AUTH_METADATA_URL", "http://localhost:1/.well-known/openid-configuration");
        Environment.SetEnvironmentVariable("AUTH_REQUIRE_HTTPS_METADATA", "false");
        Environment.SetEnvironmentVariable("JWT_ISSUER", Issuer);
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", Audience);
        Environment.SetEnvironmentVariable("ORG_ID", OrgId);
        Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", "http://localhost:8082");
        Environment.SetEnvironmentVariable("CORS_ORIGIN", "http://localhost:8080");
        Environment.SetEnvironmentVariable("ISSUER_SYNC_ENABLED", "false");
    }

    public ApiTestFactory()
    {
        SeedData();
    }

    public FakeCardRepository Cards { get; } = new();

    public FakeTransactionStore Transactions { get; } = new();

    public FakeBlobStore Blobs { get; } = new();

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
            Audience = Audience,
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
            Audience = audience ?? Audience,
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
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
                ["AZURE_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                ["SEED_ENABLED"] = "false",
                ["AUTH_METADATA_URL"] = "http://localhost:1/.well-known/openid-configuration",
                ["AUTH_REQUIRE_HTTPS_METADATA"] = "false",
                ["JWT_ISSUER"] = Issuer,
                ["JWT_AUDIENCE"] = Audience,
                ["ORG_ID"] = OrgId,
                ["PUBLIC_BASE_URL"] = "http://localhost:8082",
                ["CORS_ORIGIN"] = "http://localhost:8080",
                ["ISSUER_SYNC_ENABLED"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICardRepository>();
            services.RemoveAll<ITransactionStore>();
            services.RemoveAll<IBlobStore>();

            services.AddSingleton<ICardRepository>(Cards);
            services.AddSingleton<ITransactionStore>(Transactions);
            services.AddSingleton<IBlobStore>(Blobs);

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MetadataAddress = string.Empty;
                options.ConfigurationManager = null;
                options.RequireHttpsMetadata = false;
                options.MapInboundClaims = false;
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(SigningKey);
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = Audience;
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

    public Task<IReadOnlyList<Card>> GetByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Card>>(Cards.Where(card => card.UserId == userId).ToArray());

    public Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.SingleOrDefault(card => card.Id == cardId));
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
