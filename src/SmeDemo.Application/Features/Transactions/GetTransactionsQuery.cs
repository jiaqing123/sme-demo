using AutoMapper;
using FluentValidation;
using MediatR;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Errors;

namespace SmeDemo.Application.Features.Transactions;

public sealed record GetTransactionsQuery(
    Guid UserId,
    Guid CardId,
    string? Cursor,
    int? Limit) : IRequest<TransactionListResponse>;

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

public sealed class GetTransactionsHandler : IRequestHandler<GetTransactionsQuery, TransactionListResponse>
{
    private readonly ICardRepository _cards;
    private readonly ITransactionStore _transactions;
    private readonly IMapper _mapper;

    public GetTransactionsHandler(
        ICardRepository cards,
        ITransactionStore transactions,
        IMapper mapper)
    {
        _cards = cards;
        _transactions = transactions;
        _mapper = mapper;
    }

    public async Task<TransactionListResponse> Handle(
        GetTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var card = await _cards.GetByIdAsync(request.CardId, cancellationToken);
        if (card is null)
        {
            throw new ResourceNotFoundException("The card was not found.");
        }

        if (card.UserId != request.UserId)
        {
            throw new ResourceForbiddenException("You do not have access to this card.");
        }

        string? afterRowKey = null;
        if (request.Cursor is not null)
        {
            // The validator already rejected an undecodable cursor; this only
            // turns the opaque token back into the storage key.
            CursorCodec.TryDecode(request.Cursor, out afterRowKey);
        }

        var limit = request.Limit ?? GetTransactionsQueryValidator.DefaultLimit;
        var page = await _transactions.ReadAsync(request.CardId, afterRowKey, limit, cancellationToken);

        var items = page.Items
            .Select(record => _mapper.Map<TransactionDto>(record))
            .ToArray();

        var nextCursor = page.NextRowKey is null ? null : CursorCodec.Encode(page.NextRowKey);

        return new TransactionListResponse(items, nextCursor);
    }
}

public sealed class TransactionMappingProfile : Profile
{
    public TransactionMappingProfile()
    {
        CreateMap<TransactionRecord, TransactionDto>()
            .ConstructUsing(source => new TransactionDto(
                source.TransactionId.ToString(),
                source.CardId,
                source.AmountMinor,
                source.Currency,
                source.Direction,
                source.Merchant,
                source.Status,
                source.OccurredAt))
            .MaxDepth(64);
    }
}
