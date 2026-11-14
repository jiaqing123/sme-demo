using SmeDemo.Api.Configuration;
using SmeDemo.Api.Endpoints;
using SmeDemo.Application.Abstractions;

namespace SmeDemo.Api.Cards;

/// <summary>
/// Builds artwork URLs from the route table instead of concatenating
/// <c>/api/v1/cards/{id}/art</c> in the application layer. If the route's path
/// or version changes, this provider follows it without a second edit.
/// </summary>
public sealed class CardArtUrlProvider : ICardArtUrlProvider
{
    private readonly LinkGenerator _links;
    private readonly ApiOptions _options;

    public CardArtUrlProvider(LinkGenerator links, ApiOptions options)
    {
        _links = links;
        _options = options;
    }

    public string GetArtUrl(Guid cardId)
    {
        var path = _links.GetPathByName(ApiRoutes.CardArt, new { cardId })
            ?? throw new InvalidOperationException(
                $"No endpoint named '{ApiRoutes.CardArt}' is registered; card art URLs cannot be built.");

        return $"{_options.PublicBaseUrl.TrimEnd('/')}{path}";
    }
}
