namespace SmeDemo.Auth.Persistence;

/// <summary>
/// The deterministic identity the demo account is seeded with. The resource API
/// seeds its cards for the same user id from its own copy
/// (src/SmeDemo.Infrastructure/Seeding/DemoIdentity.cs). The services share no
/// project reference (requirements §6.7), so the values must stay in sync.
/// </summary>
internal static class DemoIdentity
{
    public static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");

    public const string OrgId = "sme";

    public const string Username = "demo";

    public const string DisplayName = "Alex Morgan";
}
