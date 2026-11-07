namespace SmeDemo.Application.Contracts;

public sealed record MeResponse(string Id, string Org, string Name);

public sealed record CardDto(
    Guid Id,
    string Brand,
    string Last4,
    byte ExpiryMonth,
    short ExpiryYear,
    string Status,
    string Currency,
    short DisplayOrder,
    string ArtUrl);

public sealed record CardListResponse(IReadOnlyList<CardDto> Items);

public sealed record TransactionDto(
    string Id,
    Guid CardId,
    long AmountMinor,
    string Currency,
    string Direction,
    string Merchant,
    string Status,
    DateTimeOffset OccurredAt);

public sealed record TransactionListResponse(
    IReadOnlyList<TransactionDto> Items,
    string? NextCursor);

public sealed record CardArtResult(Stream Content, string ContentType);
