namespace SmeDemo.Domain.Entities;

public static class CardStatus
{
    public const string Active = "active";

    public const string Frozen = "frozen";

    public const string Expired = "expired";

    public static bool IsKnown(string status) =>
        status is Active or Frozen or Expired;
}
