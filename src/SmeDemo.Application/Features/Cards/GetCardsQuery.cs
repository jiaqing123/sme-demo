using AutoMapper;
using MediatR;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Features.Cards;

public sealed record GetCardsQuery(Guid UserId) : IRequest<CardListResponse>;

public sealed class GetCardsHandler : IRequestHandler<GetCardsQuery, CardListResponse>
{
    private readonly ICardRepository _cards;
    private readonly IMapper _mapper;
    private readonly ApplicationOptions _options;

    public GetCardsHandler(ICardRepository cards, IMapper mapper, ApplicationOptions options)
    {
        _cards = cards;
        _mapper = mapper;
        _options = options;
    }

    public async Task<CardListResponse> Handle(GetCardsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cards = await _cards.GetByUserAsync(request.UserId, cancellationToken);

        var items = cards
            .OrderBy(card => card.DisplayOrder)
            .ThenBy(card => card.Id)
            .Select(card => _mapper.Map<CardDto>(card) with
            {
                ArtUrl = $"{_options.PublicBaseUrl.TrimEnd('/')}/api/v1/cards/{card.Id}/art",
            })
            .ToArray();

        return new CardListResponse(items);
    }
}

public sealed class CardMappingProfile : Profile
{
    public CardMappingProfile()
    {
        CreateMap<Card, CardDto>()
            .ConstructUsing(source => new CardDto(
                source.Id,
                source.Brand,
                source.Last4,
                source.ExpiryMonth,
                source.ExpiryYear,
                source.Status,
                source.Currency,
                source.DisplayOrder,
                string.Empty))
            .MaxDepth(64);
    }
}
