using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmeDemo.Auth.Persistence;

/// <summary>
/// Design-time only. Used by <c>dotnet ef</c> to build the model without
/// starting the service. The connection string is never used to generate the
/// migration or the SQL script.
/// </summary>
public sealed class DesignTimeAuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    private const string FallbackConnectionString =
        "Server=localhost;Database=sme_auth;User Id=sa;Password=Local_Dev_Only_123!;TrustServerCertificate=True";

    public AuthDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL") ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new AuthDbContext(options);
    }
}
