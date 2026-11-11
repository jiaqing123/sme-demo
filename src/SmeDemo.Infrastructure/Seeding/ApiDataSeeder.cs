using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmeDemo.Application.Abstractions;
using SmeDemo.Infrastructure.Persistence;
using System.Reflection;

namespace SmeDemo.Infrastructure.Seeding;

/// <summary>
/// Seeds cards and card artwork at start-up. Every write is an idempotent
/// upsert, and transient storage/database startup is retried a bounded number
/// of times; a persistent failure stops the host instead of being swallowed
/// (requirements §8.7). Transactions are pulled by <c>IssuerSyncService</c>,
/// not seeded here.
/// </summary>
public sealed class ApiDataSeeder : IHostedService
{
    private const int MaxAttempts = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly Assembly AssetAssembly = typeof(ApiDataSeeder).Assembly;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InfrastructureOptions _options;
    private readonly ILogger<ApiDataSeeder> _logger;

    public ApiDataSeeder(
        IServiceScopeFactory scopeFactory,
        InfrastructureOptions options,
        ILogger<ApiDataSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SeedAsync(cancellationToken);
                _logger.LogInformation("Demo data seeded.");
                return;
            }
            catch (Exception exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Demo data seed attempt {Attempt}/{MaxAttempts} failed; retrying in {Delay}.",
                    attempt,
                    MaxAttempts,
                    RetryDelay);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        // Probe the database first: it is a separate container that may still be
        // starting even after the migrator has completed.
        await db.Cards.AnyAsync(cancellationToken);

        // One read for all ids instead of one EXISTS query per card.
        var existingIds = await db.Cards
            .Select(card => card.Id)
            .ToHashSetAsync(cancellationToken);

        foreach (var seed in DemoSeedData.Cards)
        {
            if (!existingIds.Contains(seed.Id))
            {
                db.Cards.Add(DemoSeedData.CreateCard(seed, _options.OrgId));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var seed in DemoSeedData.Cards)
        {
            await using var artwork = OpenArtwork(seed.ArtBlobName);
            await blobs.UploadAsync(seed.ArtBlobName, artwork, "image/svg+xml", cancellationToken);
        }
    }

    private static Stream OpenArtwork(string fileName)
    {
        var suffix = $".Seeding.Assets.{fileName}";
        var resourceName = AssetAssembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded card artwork '{fileName}' was not found.");

        return AssetAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded card artwork '{fileName}' could not be opened.");
    }
}
