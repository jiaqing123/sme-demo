using System.Globalization;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Features.Transactions;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Infrastructure.Seeding;

internal sealed record DemoCard(
    Guid Id,
    string Brand,
    string Last4,
    byte ExpiryMonth,
    short ExpiryYear,
    short DisplayOrder,
    string ArtBlobName);

internal sealed record DemoTransactionSeed(
    Guid CardId,
    long AmountMinor,
    string Direction,
    string Merchant,
    string Status,
    DateTimeOffset OccurredAt);

/// <summary>
/// Deterministic seed data. Fixed timestamps and IDs make a reset reproducible
/// and make the upserts idempotent (requirements §8.7).
/// </summary>
internal static class DemoSeedData
{
    public static readonly DateTimeOffset BaseTime = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly IReadOnlyList<DemoCard> Cards =
    [
        new(
            Guid.Parse("c1000000-0000-4000-8000-000000000001"),
            "visa",
            "4242",
            9,
            2028,
            0,
            "visa-4242.svg"),
        new(
            Guid.Parse("c1000000-0000-4000-8000-000000000002"),
            "mastercard",
            "5454",
            3,
            2027,
            1,
            "mastercard-5454.svg"),
        new(
            Guid.Parse("c1000000-0000-4000-8000-000000000003"),
            "visa",
            "1881",
            11,
            2026,
            2,
            "visa-1881.svg"),
    ];

    private static readonly string[] Merchants =
    [
        "Woolworths",
        "Coles",
        "JB Hi-Fi",
        "Bunnings",
        "Uber",
        "Qantas",
        "Officeworks",
        "Bp",
        "Netflix",
        "Dan Murphy's",
    ];

    public static Card CreateCard(DemoCard seed, string orgId) =>
        Card.Create(
            seed.Id,
            DemoIdentity.UserId,
            orgId,
            seed.Brand,
            seed.Last4,
            seed.ExpiryMonth,
            seed.ExpiryYear,
            "AUD",
            seed.DisplayOrder,
            seed.ArtBlobName);

    public static IReadOnlyList<TransactionRecord> CreateTransactions()
    {
        var seeds = new List<DemoTransactionSeed>(40);
        AddTransactions(seeds, Cards[0].Id, count: 14, firstOffsetHours: -3, spacingHours: 3);
        AddTransactions(seeds, Cards[1].Id, count: 13, firstOffsetHours: -2, spacingHours: 5);
        AddTransactions(seeds, Cards[2].Id, count: 13, firstOffsetHours: -1, spacingHours: 4);

        return seeds
            .Select((seed, index) =>
            {
                var transactionId = DeterministicTransactionId(index + 1);
                var rowKey = TransactionKeys.CreateRowKey(seed.OccurredAt, transactionId);

                return new TransactionRecord(
                    rowKey,
                    seed.CardId,
                    transactionId,
                    seed.AmountMinor,
                    "AUD",
                    seed.Direction,
                    seed.Merchant,
                    seed.Status,
                    seed.OccurredAt);
            })
            .ToArray();
    }

    private static void AddTransactions(
        List<DemoTransactionSeed> seeds,
        Guid cardId,
        int count,
        int firstOffsetHours,
        int spacingHours)
    {
        for (var index = 0; index < count; index++)
        {
            var occurredAt = BaseTime.AddHours(firstOffsetHours - (index * spacingHours));
            var isCredit = index % 7 == 6;
            var amount = isCredit
                ? 5000 + (index * 1375)
                : 450 + (index * 1031);

            seeds.Add(new DemoTransactionSeed(
                cardId,
                amount,
                isCredit ? "credit" : "debit",
                Merchants[(index + (int)cardId.ToString("N")[0]) % Merchants.Length],
                index % 9 == 8 ? "pending" : "posted",
                occurredAt));
        }
    }

    private static Guid DeterministicTransactionId(int sequence) =>
        Guid.Parse(string.Create(
            CultureInfo.InvariantCulture,
            $"00000000-0000-4000-8000-{sequence:D12}"));
}
