using SmeDemo.Migrator;

try
{
    return await MigratorRunner.RunAsync();
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync($"[migrator] FAILED: {exception.Message}");
    return 1;
}
