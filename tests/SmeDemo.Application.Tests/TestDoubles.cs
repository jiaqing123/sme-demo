using AutoMapper;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Features.Cards;
using SmeDemo.Application.Features.Transactions;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Tests;

internal sealed class FakeCardRepository : ICardRepository
{
    public List<Card> Cards { get; } = [];

    public Task<IReadOnlyList<Card>> GetByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Card>>(Cards.Where(card => card.UserId == userId).ToArray());

    public Task<Card?> GetByIdAsync(Guid cardId, CancellationToken cancellationToken) =>
        Task.FromResult(Cards.SingleOrDefault(card => card.Id == cardId));
}

internal sealed class FakeTransactionStore : ITransactionStore
{
    public List<TransactionRecord> Records { get; } = [];

    public int? LastLimit { get; private set; }

    public string? LastCursor { get; private set; }

    public Task<TransactionPage> ReadAsync(
        Guid cardId,
        string? afterRowKey,
        int limit,
        CancellationToken cancellationToken)
    {
        LastLimit = limit;
        LastCursor = afterRowKey;

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

internal static class TestData
{
    public static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");
    public static readonly Guid OtherUserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000002");

    public static Card CreateCard(Guid id, Guid userId, short displayOrder, string last4 = "4242") =>
        Card.Create(
            id,
            userId,
            "sme",
            "visa",
            last4,
            (byte)9,
            (short)2028,
            "AUD",
            displayOrder,
            $"art-{last4}.svg");

    public static TransactionRecord CreateTransaction(Guid cardId, int sequence)
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

    public static IMapper CreateMapper()
    {
        var configuration = new MapperConfiguration(config =>
        {
            config.AddProfile<CardMappingProfile>();
            config.AddProfile<TransactionMappingProfile>();
        });

        return configuration.CreateMapper();
    }

    public static ApplicationOptions Options() => new("http://localhost:8082");
}
