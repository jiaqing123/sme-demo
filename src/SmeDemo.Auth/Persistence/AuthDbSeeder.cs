using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Persistence;

/// <summary>
/// Seeds the single demo user. This is data, not schema: the schema is applied by
/// the one-shot migrator before this service starts.
/// </summary>
public static class AuthDbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, AuthOptions options, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AuthUser>>();

        if (!options.DemoMode)
        {
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return;
            }

            var user = AuthUser.Create(
                Guid.NewGuid(),
                options.OrgId,
                options.DemoUsername,
                DemoIdentity.DisplayName,
                options.DemoPhone,
                DateTime.UtcNow);

            user.SetPasswordHash(hasher.HashPassword(user, options.DemoPassword));
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var demoUser = await db.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == DemoIdentity.UserId, cancellationToken)
            ?? await db.Users
                .SingleOrDefaultAsync(candidate => candidate.Username == options.DemoUsername, cancellationToken);

        if (demoUser is null)
        {
            demoUser = AuthUser.Create(
                DemoIdentity.UserId,
                options.OrgId,
                options.DemoUsername,
                DemoIdentity.DisplayName,
                options.DemoPhone,
                DateTime.UtcNow);
            demoUser.SetPasswordHash(hasher.HashPassword(demoUser, options.DemoPassword));
            db.Users.Add(demoUser);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // A volume created before the deterministic id was introduced holds a
        // random GUID for the same demo user. No foreign key crosses the two
        // databases, and there is exactly one demo user, so rewriting the key in
        // place is safe and keeps the API's cards attached to this account. EF
        // refuses to modify a tracked primary key, so the row is updated directly
        // and the entity is reloaded under its new key.
        if (demoUser.Id != DemoIdentity.UserId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [auth].[users] SET [id] = {DemoIdentity.UserId} WHERE [id] = {demoUser.Id}",
                cancellationToken);

            db.Entry(demoUser).State = EntityState.Detached;
            demoUser = await db.Users.SingleAsync(
                candidate => candidate.Id == DemoIdentity.UserId,
                cancellationToken);
        }

        demoUser.UpdateProfile(options.OrgId, options.DemoUsername, DemoIdentity.DisplayName, options.DemoPhone);

        // Changing DEMO_PASSWORD in .env must take effect on an existing volume,
        // not just on a fresh one.
        if (hasher.VerifyHashedPassword(demoUser, demoUser.PasswordHash, options.DemoPassword)
            != PasswordVerificationResult.Success)
        {
            demoUser.SetPasswordHash(hasher.HashPassword(demoUser, options.DemoPassword));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
