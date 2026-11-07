using System.Text.Json;

namespace SmeDemo.IssuerMock;

/// <summary>
/// Loads the embedded fixture once and serves each card's raw JSON object
/// unchanged. The fixture is the demo contract (requirements §13.4): fixed IDs,
/// timestamps, amounts and ordering make resets and screenshots deterministic.
/// </summary>
internal sealed class IssuerFixture
{
    private const string ResourceSuffix = "fixtures.transactions.json";

    private readonly IReadOnlyDictionary<Guid, string> _byCard;

    private IssuerFixture(IReadOnlyDictionary<Guid, string> byCard)
    {
        _byCard = byCard;
    }

    public static IssuerFixture Load()
    {
        var assembly = typeof(IssuerFixture).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The embedded issuer fixture was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded issuer fixture could not be opened.");
        using var document = JsonDocument.Parse(stream);

        var byCard = new Dictionary<Guid, string>();
        foreach (var card in document.RootElement.GetProperty("cards").EnumerateArray())
        {
            var cardId = card.GetProperty("card_id").GetGuid();
            byCard[cardId] = card.GetRawText();
        }

        return new IssuerFixture(byCard);
    }

    public bool TryGetRaw(Guid cardId, out string raw) => _byCard.TryGetValue(cardId, out raw!);
}
