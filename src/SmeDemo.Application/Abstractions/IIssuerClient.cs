namespace SmeDemo.Application.Abstractions;

/// <summary>
/// One transaction as the card issuer returned it. The payload is untrusted
/// input: <c>IssuerSyncService</c> validates every field before it reaches the
/// transaction store. <see cref="RawPayload"/> is the exact issuer JSON object
/// for the transaction, kept for traceability (requirements §8.5).
/// </summary>
public sealed record IssuerTransaction(
    Guid CardId,
    Guid TransactionId,
    long AmountMinor,
    string Currency,
    string Direction,
    string Merchant,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset PostedAt,
    string RawPayload);

/// <summary>
/// The card issuer seam. The demo adapter calls <c>issuer-mock</c>; replacing it
/// with a real issuer is an adapter change, not an application change.
/// </summary>
public interface IIssuerClient
{
    Task<IReadOnlyList<IssuerTransaction>> GetTransactionsAsync(
        Guid cardId,
        CancellationToken cancellationToken);
}
