using Microsoft.Data.SqlClient;

namespace SmeDemo.Migrator;

/// <summary>
/// Applies the committed, idempotent schema scripts to a database. It waits for
/// SQL Server, creates the database when missing, then runs each script batch by
/// batch. An optional bootstrap script runs afterwards on the same connection,
/// with <c>${NAME}</c> tokens expanded from the environment. The generated
/// scripts manage their own transactions; this tool only guarantees that a
/// failure stops everything and is reported as a non-zero exit.
/// </summary>
internal static class MigratorRunner
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync()
    {
        var settings = MigratorSettings.FromEnvironment();
        Console.WriteLine($"[migrator] database={settings.DatabaseName}");
        Console.WriteLine($"[migrator] scripts={settings.MigrationsPath}");

        string? bootstrapSql = null;
        if (settings.BootstrapSqlPath is { } bootstrapPath)
        {
            // Fail before waiting on SQL Server or applying migrations: a
            // missing file or variable is a deployment configuration error, not
            // a transient database problem, and must not leave a half-applied
            // change behind.
            if (!File.Exists(bootstrapPath))
            {
                throw new FileNotFoundException(
                    $"BOOTSTRAP_SQL file not found: {bootstrapPath}",
                    bootstrapPath);
            }

            var template = await File.ReadAllTextAsync(bootstrapPath);
            bootstrapSql = SqlVariableSubstitution.SubstituteFromEnvironment(template);
            Console.WriteLine($"[migrator] bootstrap={bootstrapPath}");
        }

        await WaitForServerAsync(settings);
        await EnsureDatabaseAsync(settings);
        await WaitForDatabaseAsync(settings);

        var scripts = Directory.Exists(settings.MigrationsPath)
            ? Directory.GetFiles(settings.MigrationsPath, "*.sql").Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        if (scripts.Length == 0)
        {
            if (bootstrapSql is null)
            {
                Console.WriteLine("[migrator] no scripts found; nothing to do.");
                return 0;
            }

            Console.WriteLine("[migrator] no scripts found.");
        }
        else
        {
            foreach (var script in scripts)
            {
                await ApplyScriptAsync(settings, script);
            }

            Console.WriteLine($"[migrator] applied {scripts.Length} script(s).");
        }

        if (bootstrapSql is not null)
        {
            await ApplyBootstrapAsync(settings, bootstrapSql);
        }

        return 0;
    }

    private static async Task WaitForServerAsync(MigratorSettings settings)
    {
        var deadline = DateTime.UtcNow + settings.WaitTimeout;
        SqlException? last = null;

        for (var attempt = 1; attempt <= settings.MaxWaitAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(settings.MasterConnectionString);
                await connection.OpenAsync();
                Console.WriteLine($"[migrator] SQL Server reachable after {attempt} attempt(s).");
                return;
            }
            catch (SqlException exception)
            {
                last = exception;
                if (attempt >= settings.MaxWaitAttempts || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Console.WriteLine($"[migrator] waiting for SQL Server (attempt {attempt}): {exception.Message}");
                await Task.Delay(RetryDelay);
            }
        }

        throw new TimeoutException(
            $"SQL Server did not accept a connection within {settings.WaitTimeout} " +
            $"({settings.MaxWaitAttempts} attempts). Is the database ready?",
            last);
    }

    /// <summary>
    /// Waits until the target database can actually be opened. Being able to
    /// open the <c>master</c> connection is not the same thing: a database that
    /// was just created, or one SQL Server is still recovering after an unclean
    /// stop, answers "Cannot open database ... The login failed." until it is
    /// online. Without this wait the migrator fails on the first script and the
    /// one-shot containers gate every other service behind a non-zero exit.
    /// </summary>
    private static async Task WaitForDatabaseAsync(MigratorSettings settings)
    {
        var deadline = DateTime.UtcNow + settings.WaitTimeout;
        SqlException? last = null;

        for (var attempt = 1; attempt <= settings.MaxWaitAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(settings.ConnectionString);
                await connection.OpenAsync();
                Console.WriteLine($"[migrator] database {settings.DatabaseName} is online after {attempt} attempt(s).");
                return;
            }
            catch (SqlException exception)
            {
                last = exception;
                if (attempt >= settings.MaxWaitAttempts || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                Console.WriteLine(
                    $"[migrator] waiting for database {settings.DatabaseName} " +
                    $"(attempt {attempt}): {exception.Message}");
                await Task.Delay(RetryDelay);
            }
        }

        throw new TimeoutException(
            $"database {settings.DatabaseName} did not come online within {settings.WaitTimeout} " +
            $"({settings.MaxWaitAttempts} attempts).",
            last);
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

        await ExecuteBatchesAsync(settings, batches);

        Console.WriteLine($"[migrator] applied {Path.GetFileName(scriptPath)}.");
    }

    private static async Task ApplyBootstrapAsync(MigratorSettings settings, string sql)
    {
        var batches = SqlScript.SplitBatches(sql);

        await ExecuteBatchesAsync(settings, batches);

        // The expanded SQL is never logged: it contains the application passwords.
        Console.WriteLine($"[migrator] applied bootstrap ({batches.Count} batch(es)).");
    }

    private static async Task ExecuteBatchesAsync(MigratorSettings settings, IReadOnlyList<string> batches)
    {
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
    }
}
