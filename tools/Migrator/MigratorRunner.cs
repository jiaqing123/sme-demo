using Microsoft.Data.SqlClient;

namespace SmeDemo.Migrator;

/// <summary>
/// Applies the committed, idempotent schema scripts to a database. It waits for
/// SQL Server, creates the database when missing, then runs each script batch by
/// batch. The generated scripts manage their own transactions; this tool only
/// guarantees that a failure stops everything and is reported as a non-zero exit.
/// </summary>
internal static class MigratorRunner
{
    public static async Task<int> RunAsync()
    {
        var settings = MigratorSettings.FromEnvironment();
        Console.WriteLine($"[migrator] database={settings.DatabaseName}");
        Console.WriteLine($"[migrator] scripts={settings.MigrationsPath}");

        await WaitForServerAsync(settings);
        await EnsureDatabaseAsync(settings);

        var scripts = Directory.Exists(settings.MigrationsPath)
            ? Directory.GetFiles(settings.MigrationsPath, "*.sql").Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        if (scripts.Length == 0)
        {
            Console.WriteLine("[migrator] no scripts found; nothing to do.");
            return 0;
        }

        foreach (var script in scripts)
        {
            await ApplyScriptAsync(settings, script);
        }

        Console.WriteLine($"[migrator] applied {scripts.Length} script(s).");
        return 0;
    }

    private static async Task WaitForServerAsync(MigratorSettings settings)
    {
        var deadline = DateTime.UtcNow + settings.WaitTimeout;

        for (var attempt = 1; attempt <= settings.MaxWaitAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(settings.MasterConnectionString);
                await connection.OpenAsync();
                Console.WriteLine($"[migrator] SQL Server reachable after {attempt} attempt(s).");
                return;
            }
            catch (SqlException exception) when (DateTime.UtcNow < deadline)
            {
                Console.WriteLine($"[migrator] waiting for SQL Server (attempt {attempt}): {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static async Task EnsureDatabaseAsync(MigratorSettings settings)
    {
        await using var connection = new SqlConnection(settings.MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText =
            """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
                PRINT 'created database ' + @name;
            END
            """;
        command.Parameters.AddWithValue("@name", settings.DatabaseName);

        await command.ExecuteNonQueryAsync();
        Console.WriteLine($"[migrator] database {settings.DatabaseName} present.");
    }

    private static async Task ApplyScriptAsync(MigratorSettings settings, string scriptPath)
    {
        var sql = await File.ReadAllTextAsync(scriptPath);
        var batches = SqlScript.SplitBatches(sql);
        Console.WriteLine($"[migrator] applying {Path.GetFileName(scriptPath)} ({batches.Count} batch(es))");

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync();

        // Let any failed statement abort the whole batch instead of leaving a
        // half-applied migration behind.
        await using (var abort = connection.CreateCommand())
        {
            abort.CommandText = "SET XACT_ABORT ON;";
            await abort.ExecuteNonQueryAsync();
        }

        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"[migrator] applied {Path.GetFileName(scriptPath)}.");
    }
}
