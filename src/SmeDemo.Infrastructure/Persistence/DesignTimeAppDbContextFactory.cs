using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmeDemo.Infrastructure.Persistence;

/// <summary>
/// Design-time only. Used by <c>dotnet ef</c> to build a model without starting
/// the API. The connection string is never used to connect; migrations and
/// scripts are generated from the model alone.
/// </summary>
public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string FallbackConnectionString =
        "Server=localhost;Database=sme_api;User Id=sa;Password=Local_Dev_Only_123!;TrustServerCertificate=True";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL") ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new AppDbContext(options);
    }
}
