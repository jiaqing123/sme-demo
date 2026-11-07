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

/// <summary>
/// Deterministic card and artwork seed data. Transactions are not seeded: the
/// mock issuer is the source of truth and <c>IssuerSyncService</c> writes them
/// to Table Storage (requirements §8.7).
/// </summary>
internal static class DemoSeedData
{
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
}
