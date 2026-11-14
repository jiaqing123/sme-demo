using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmeDemo.Application.Abstractions;
using SmeDemo.Domain.Entities;
using SmeDemo.Infrastructure.Issuer;
using SmeDemo.Infrastructure.Persistence;

namespace SmeDemo.Infrastructure.Tests;

internal sealed class FakeIssuerClient : IIssuerClient
{
    public Dictionary<Guid, IReadOnlyList<IssuerTransaction>> Responses { get; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<IssuerTransaction>> GetTransactionsAsync(
        Guid cardId,
        CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            return Task.FromException<IReadOnlyList<IssuerTransaction>>(Failure);
        }

        return Task.FromResult(
            Responses.TryGetValue(cardId, out var transactions)
                ? transactions
                : Array.Empty<IssuerTransaction>());
    }
}

internal sealed class FakeTransactionStore : ITransactionStore
{
    public List<TransactionRecord> Records { get; } = [];

    public int UpsertCalls { get; private set; }

    public Task<TransactionPage> ReadAsync(
        Guid cardId,
        string? afterRowKey,
        int limit,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The sync service never reads the feed.");

    public Task UpsertAsync(
        IReadOnlyCollection<TransactionRecord> records,
        CancellationToken cancellationToken)
    {
        UpsertCalls++;

        foreach (var record in records)
        {
            var existing = Records.FindIndex(item => item.RowKey == record.RowKey);
            if (existing >= 0)
            {
                Records[existing] = record;
            }
            else
            {
                Records.Add(record);
            }
        }

        return Task.CompletedTask;
    }
}

internal sealed class SyncTestHost : IAsyncDisposable
{
    public const string ApiKey = "test-issuer-key";

    public static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    public static readonly Guid SecondCardId = Guid.Parse("c1000000-0000-4000-8000-000000000002");
    public static readonly Guid ThirdCardId = Guid.Parse("c1000000-0000-4000-8000-000000000003");

    public static readonly DateTimeOffset BaseTime =
        new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    private SyncTestHost(
        ServiceProvider provider,
        FakeIssuerClient issuer,
        FakeTransactionStore store,
        IssuerSyncService service)
    {
        _provider = provider;
        Issuer = issuer;
        Store = store;
        Service = service;
    }

    public FakeIssuerClient Issuer { get; }

    public FakeTransactionStore Store { get; }

    public IssuerSyncService Service { get; }

    public static SyncTestHost Create()
    {
        var options = new InfrastructureOptions(
            "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
            "UseDevelopmentStorage=true",
            "transactions",
            "card-art",
            "sme",
            "http://issuer-mock:8084",
            ApiKey,
            TimeSpan.FromMinutes(5),
            IssuerSyncEnabled: true);

        var issuer = new FakeIssuerClient();
        var store = new FakeTransactionStore();
        var databaseName = $"issuer-sync-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(db => db.UseInMemoryDatabase(databaseName));
        services.AddSingleton(options);
        services.AddSingleton<IIssuerClient>(issuer);
        services.AddSingleton<ITransactionStore>(store);

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Cards.AddRange(CreateCards());
            db.SaveChanges();
        }

        var service = new IssuerSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<IssuerSyncService>.Instance);

        return new SyncTestHost(provider, issuer, store, service);
    }

    public static IssuerTransaction Transaction(
        Guid cardId,
        int sequence,
        long amountMinor = 1000,
        string direction = "debit",
        string status = "posted")
    {
        var occurredAt = BaseTime.AddHours(-sequence);
        return new IssuerTransaction(
            cardId,
            Guid.Parse($"00000000-0000-4000-8000-{sequence:D12}"),
            amountMinor,
            "AUD",
            direction,
            "Woolworths",
            status,
            occurredAt,
            occurredAt.AddHours(2),
            $$"""{"transaction_id":"00000000-0000-4000-8000-{{sequence:D12}}"}""");
    }

    public async Task<IssuerSync[]> GetSyncRowsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.IssuerSyncs.AsNoTracking().ToArrayAsync();
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    private static Card[] CreateCards() =>
    [
        Card.Create(FirstCardId, Guid.Parse("7f3c2a10-0000-4000-8000-000000000001"), "sme", "visa", "4242", 9, 2028, "AUD", 0, "visa-4242.svg"),
        Card.Create(SecondCardId, Guid.Parse("7f3c2a10-0000-4000-8000-000000000001"), "sme", "mastercard", "5454", 3, 2027, "AUD", 1, "mastercard-5454.svg"),
        Card.Create(ThirdCardId, Guid.Parse("7f3c2a10-0000-4000-8000-000000000001"), "sme", "visa", "1881", 11, 2026, "AUD", 2, "visa-1881.svg"),
    ];
}
