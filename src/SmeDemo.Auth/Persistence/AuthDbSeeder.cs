using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Persistence;

/// <summary>
/// Seeds the single demo user when the table is empty. This is data, not schema:
/// the schema is applied by the one-shot migrator before this service starts.
/// </summary>
public static class AuthDbSeeder
{
    private const string DemoDisplayName = "Alex Morgan";

    public static async Task SeedAsync(IServiceProvider services, AuthOptions options, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUser>>();
        var user = AuthUser.Create(
            Guid.NewGuid(),
            options.OrgId,
            options.DemoUsername,
            DemoDisplayName,
            options.DemoPhone,
            DateTime.UtcNow);

        user.SetPasswordHash(hasher.HashPassword(user, options.DemoPassword));
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
    }
}
