using Microsoft.Data.SqlClient;

namespace SmeDemo.Migrator;

internal sealed record MigratorSettings(
    string ConnectionString,
    string DatabaseName,
    string MigrationsPath,
    int MaxWaitAttempts,
    TimeSpan WaitTimeout)
{
    public string MasterConnectionString =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;

    public static MigratorSettings FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("DATABASE_URL is required.");
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException("DATABASE_URL must name a database.");
        }

        var migrationsPath = Environment.GetEnvironmentVariable("MIGRATIONS_PATH") ?? "/app/migrations";

        var maxWaitAttempts = int.TryParse(Environment.GetEnvironmentVariable("DB_MAX_ATTEMPTS"), out var attempts)
            ? attempts
            : 60;

        var waitTimeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("DB_WAIT_SECONDS"), out var seconds)
                ? seconds
                : 120);

        return new MigratorSettings(connectionString, builder.InitialCatalog, migrationsPath, maxWaitAttempts, waitTimeout);
    }
}
