using SmeDemo.Application.Abstractions;

namespace SmeDemo.Api.Readiness;

/// <summary>
/// The outcome of one readiness check. Only the dependency's stable name and a
/// boolean ever leave the server; exception messages and connection strings are
/// deliberately discarded by the orchestrator.
/// </summary>
public sealed record ReadinessCheck(string Name, bool Ready);

/// <summary>
/// A point-in-time readiness snapshot. <see cref="IsReady"/> is true only when
/// every configured dependency reported ready.
/// </summary>
public sealed record ReadinessSnapshot(IReadOnlyList<ReadinessCheck> Checks)
{
    public bool IsReady => Checks.Count > 0 && Checks.All(check => check.Ready);
}

/// <summary>
/// Runs every registered <see cref="IReadinessProbe"/> in parallel under one
/// short overall deadline. Keeping this orchestration outside the endpoint
/// layer leaves the endpoint responsible only for HTTP mapping.
/// </summary>
public interface IReadinessService
{
    Task<ReadinessSnapshot> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Readiness is polled by Compose, orchestrator probes and people with curl, so
/// every call must not fan out three downstream calls. A snapshot is cached for
/// <see cref="CacheDuration"/> and refreshes are single-flight: concurrent
/// probes share one in-flight check instead of stampeding SQL Server, Blob
/// Storage and auth. The service is a singleton because the cache must outlive
/// a request; probes are resolved from a fresh scope so a scoped probe (SQL
/// connection, HTTP client) is still created and disposed correctly.
/// </summary>
public sealed class ReadinessService : IReadinessService
{
    /// <summary>
    /// The probe budget. It is intentionally shorter than the Compose
    /// healthcheck timeout so a hung dependency produces a fast 503 instead of
    /// blocking the healthcheck until Docker kills it.
    /// </summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a snapshot is reused. Short enough that a recovered dependency
    /// shows up on the next probe interval, long enough that a chatty probe does
    /// not multiply downstream load.
    /// </summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ReadinessSnapshot? _snapshot;
    private DateTimeOffset _snapshotAt;

    public ReadinessService(IServiceScopeFactory scopeFactory, TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
    }

    public async Task<ReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        if (TryReadCache(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed the snapshot while this one
            // waited for the gate.
            if (TryReadCache(out cached))
            {
                return cached;
            }

            using var scope = _scopeFactory.CreateScope();
            var probes = scope.ServiceProvider.GetServices<IReadinessProbe>();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(OverallTimeout);

            var checks = await Task.WhenAll(
                probes.Select(probe => CheckAsync(probe, deadline.Token)));

            var snapshot = new ReadinessSnapshot(checks);
            _snapshotAt = _clock.GetUtcNow();
            Volatile.Write(ref _snapshot, snapshot);

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReadCache(out ReadinessSnapshot snapshot)
    {
        var cached = Volatile.Read(ref _snapshot);
        if (cached is not null && _clock.GetUtcNow() - _snapshotAt < CacheDuration)
        {
            snapshot = cached;
            return true;
        }

        snapshot = null!;
        return false;
    }

    private static async Task<ReadinessCheck> CheckAsync(
        IReadinessProbe probe,
        CancellationToken cancellationToken)
    {
        try
        {
            // WaitAsync is a second guard: even a probe that ignores its token
            // cannot hold the response past the deadline.
            var ready = await probe.CheckAsync(cancellationToken).WaitAsync(cancellationToken);
            return new ReadinessCheck(probe.Name, ready);
        }
        catch (OperationCanceledException)
        {
            return new ReadinessCheck(probe.Name, false);
        }
        catch (Exception)
        {
            // The body must not carry exception detail. The dependency name and
            // degraded state are all an operator needs to start investigating.
            return new ReadinessCheck(probe.Name, false);
        }
    }
}
