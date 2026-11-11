using FluentValidation;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Errors;

namespace SmeDemo.Application.Features.Transactions;

public sealed record GetTransactionsQuery(
    Guid UserId,
    Guid CardId,
    string? Cursor,
    int? Limit) : IQuery<TransactionListResponse>;

public sealed class GetTransactionsQueryValidator : AbstractValidator<GetTransactionsQuery>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public GetTransactionsQueryValidator()
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, MaxLimit)
            .When(query => query.Limit.HasValue)
            .WithMessage($"The limit must be between 1 and {MaxLimit}.");

        RuleFor(query => query.Cursor)
            .Must(cursor => cursor is null || CursorCodec.TryDecode(cursor, out _))
            .WithMessage("The cursor is not valid.");
    }
}

public sealed class GetTransactionsHandler : IQueryHandler<GetTransactionsQuery, TransactionListResponse>
{
    private readonly ICardRepository _cards;
    private readonly ITransactionStore _transactions;

    public GetTransactionsHandler(
        ICardRepository cards,
        ITransactionStore transactions)
    {
        _cards = cards;
        _transactions = transactions;
    }

    public async Task<TransactionListResponse> HandleAsync(
        GetTransactionsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var card = await _cards.GetByIdAsync(query.CardId, cancellationToken);
        if (card is null)
        {
            throw new ResourceNotFoundException("The card was not found.");
        }

        if (card.UserId != query.UserId)
        {
            throw new ResourceForbiddenException("You do not have access to this card.");
        }

        string? afterRowKey = null;
        if (query.Cursor is not null)
        {
            // The validator already rejected an undecodable cursor; this only
            // turns the opaque token back into the storage key.
            CursorCodec.TryDecode(query.Cursor, out afterRowKey);
        }

        var limit = query.Limit ?? GetTransactionsQueryValidator.DefaultLimit;
        var page = await _transactions.ReadAsync(query.CardId, afterRowKey, limit, cancellationToken);

        // Hand-written mapping: one flat projection, visible at the call site.
        var items = page.Items
            .Select(record => new TransactionDto(
                record.TransactionId.ToString(),
                record.CardId,
                record.AmountMinor,
                record.Currency,
                record.Direction,
                record.Merchant,
                record.Status,
                record.OccurredAt))
            .ToArray();

        var nextCursor = page.NextRowKey is null ? null : CursorCodec.Encode(page.NextRowKey);

        return new TransactionListResponse(items, nextCursor);
    }
}
