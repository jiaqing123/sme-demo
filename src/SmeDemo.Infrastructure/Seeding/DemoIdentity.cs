namespace SmeDemo.Infrastructure.Seeding;

/// <summary>
/// The demo account the API seeds its cards for. The auth service seeds the same
/// identity from its own copy of these constants
/// (src/SmeDemo.Auth/Persistence/DemoIdentity.cs); the two values must stay in
/// sync, because the services share no project reference (requirements §6.7).
/// </summary>
internal static class DemoIdentity
{
    public static readonly Guid UserId = Guid.Parse("7f3c2a10-0000-4000-8000-000000000001");

    public const string Username = "demo";
}
