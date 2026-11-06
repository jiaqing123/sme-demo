namespace SmeDemo.Domain.Entities;

/// <summary>
/// A payment card as the wallet displays it. Only the last four digits of the
/// number are ever stored; a full PAN, CVV, or PIN must never reach this type.
/// </summary>
public sealed class Card
{
    public const int Last4Length = 4;

    private Card()
    {
        // EF Core.
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string OrgId { get; private set; } = string.Empty;

    public string Brand { get; private set; } = string.Empty;

    public string Last4 { get; private set; } = string.Empty;

    public byte ExpiryMonth { get; private set; }

    public short ExpiryYear { get; private set; }

    public string Status { get; private set; } = CardStatus.Active;

    public string Currency { get; private set; } = string.Empty;

    public short DisplayOrder { get; private set; }

    public string ArtBlobName { get; private set; } = string.Empty;

    public static Card Create(
        Guid id,
        Guid userId,
        string orgId,
        string brand,
        string last4,
        byte expiryMonth,
        short expiryYear,
        string currency,
        short displayOrder,
        string artBlobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brand);
        ArgumentException.ThrowIfNullOrWhiteSpace(artBlobName);

        if (last4.Length != Last4Length || !last4.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Last4 must be exactly four digits.", nameof(last4));
        }

        if (expiryMonth is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "Expiry month must be 1-12.");
        }

        if (currency.Length != 3)
        {
            throw new ArgumentException("Currency must be an ISO 4217 code.", nameof(currency));
        }

        return new Card
        {
            Id = id,
            UserId = userId,
            OrgId = orgId,
            Brand = brand,
            Last4 = last4,
            ExpiryMonth = expiryMonth,
            ExpiryYear = expiryYear,
            Status = CardStatus.Active,
            Currency = currency,
            DisplayOrder = displayOrder,
            ArtBlobName = artBlobName,
        };
    }
}
