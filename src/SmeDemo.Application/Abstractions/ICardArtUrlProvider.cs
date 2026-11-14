namespace SmeDemo.Application.Abstractions;

/// <summary>
/// Resolves the public URL of a card's artwork. The application layer names the
/// card, not the HTTP route: the API supplies the route path from its own
/// endpoint table so a version or route change cannot leave a stale hardcoded
/// path in a handler.
/// </summary>
public interface ICardArtUrlProvider
{
    string GetArtUrl(Guid cardId);
}
