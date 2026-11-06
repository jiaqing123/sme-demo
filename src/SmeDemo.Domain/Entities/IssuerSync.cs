namespace SmeDemo.Domain.Entities;

/// <summary>
/// The last known result of pulling transactions for one card from the card
/// issuer. One row per card; it is the only place that records integration
/// state, which is what makes the third-party dependency visible.
/// </summary>
public sealed class IssuerSync
{
    private IssuerSync()
    {
        // EF Core.
    }

    public Guid CardId { get; private set; }

    public DateTime LastSyncedAt { get; private set; }

    public string LastStatus { get; private set; } = IssuerSyncStatus.Ok;

    public string? LastError { get; private set; }

    public static IssuerSync Succeeded(Guid cardId, DateTime syncedAtUtc) => new()
    {
        CardId = cardId,
        LastSyncedAt = syncedAtUtc,
        LastStatus = IssuerSyncStatus.Ok,
        LastError = null,
    };

    public static IssuerSync Failed(Guid cardId, DateTime syncedAtUtc, string error) => new()
    {
        CardId = cardId,
        LastSyncedAt = syncedAtUtc,
        LastStatus = IssuerSyncStatus.Failed,
        LastError = error.Length > 512 ? error[..512] : error,
    };
}
