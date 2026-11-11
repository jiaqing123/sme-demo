using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Errors;

namespace SmeDemo.Application.Features.Cards;

public sealed record GetCardArtQuery(Guid UserId, Guid CardId) : IQuery<CardArtResult>;

public sealed class GetCardArtHandler : IQueryHandler<GetCardArtQuery, CardArtResult>
{
    private readonly ICardRepository _cards;
    private readonly IBlobStore _blobs;

    public GetCardArtHandler(ICardRepository cards, IBlobStore blobs)
    {
        _cards = cards;
        _blobs = blobs;
    }

    public async Task<CardArtResult> HandleAsync(
        GetCardArtQuery query,
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

        var blob = await _blobs.OpenReadAsync(card.ArtBlobName, cancellationToken)
            ?? throw new ResourceNotFoundException("The card artwork was not found.");

        return new CardArtResult(blob.Content, blob.ContentType);
    }
}
