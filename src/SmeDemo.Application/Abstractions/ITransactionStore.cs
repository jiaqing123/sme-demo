namespace SmeDemo.Application.Abstractions;

/// <summary>
/// One transaction as the feed stores and returns it. <see cref="RowKey"/> is the
/// storage cursor; it is wrapped as an opaque token before it reaches a client.
/// <see cref="PostedAt"/> and <see cref="IssuerPayload"/> come from the issuer
/// and are persisted as supplied; older rows may not have them.
/// </summary>
public sealed record TransactionRecord(
    string RowKey,
    Guid CardId,
    Guid TransactionId,
    long AmountMinor,
    string Currency,
    string Direction,
    string Merchant,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset PostedAt,
    string IssuerPayload);

public sealed record TransactionPage(IReadOnlyList<TransactionRecord> Items, string? NextRowKey);

/// <summary>
/// The transaction feed. Table Storage is the only adapter today; Cosmos DB is a
/// later option behind the same port (requirements §8.6).
/// </summary>
public interface ITransactionStore
{
    Task<TransactionPage> ReadAsync(
        Guid cardId,
        string? afterRowKey,
        int limit,
        CancellationToken cancellationToken);

    Task UpsertAsync(IReadOnlyCollection<TransactionRecord> records, CancellationToken cancellationToken);
}
