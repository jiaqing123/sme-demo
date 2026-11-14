using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Features.Cards;

public sealed record GetCardsQuery(Guid UserId) : IQuery<CardListResponse>;

public sealed class GetCardsHandler : IQueryHandler<GetCardsQuery, CardListResponse>
{
    private readonly ICardRepository _cards;
    private readonly ICardArtUrlProvider _artUrls;

    public GetCardsHandler(ICardRepository cards, ICardArtUrlProvider artUrls)
    {
        _cards = cards;
        _artUrls = artUrls;
    }

    public async Task<CardListResponse> HandleAsync(
        GetCardsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var cards = await _cards.GetByUserAsync(query.UserId, cancellationToken);

        var items = cards
            .OrderBy(card => card.DisplayOrder)
            .ThenBy(card => card.Id)
            .Select(ToDto)
            .ToArray();

        return new CardListResponse(items);
    }

    // Hand-written mapping: the projection is flat, so an explicit constructor
    // call is cheaper to read and debug than a configuration-based mapper.
    private CardDto ToDto(Card card) => new(
        card.Id,
        card.Brand,
        card.Last4,
        card.ExpiryMonth,
        card.ExpiryYear,
        card.Status,
        card.Currency,
        card.DisplayOrder,
        _artUrls.GetArtUrl(card.Id));
}
