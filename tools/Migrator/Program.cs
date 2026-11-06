using SmeDemo.Migrator;

try
{
    return await MigratorRunner.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"[migrator] FAILED: {exception.Message}");
    return 1;
}
