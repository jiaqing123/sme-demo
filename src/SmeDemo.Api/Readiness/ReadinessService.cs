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

public sealed class ReadinessService(IEnumerable<IReadinessProbe> probes) : IReadinessService
{
    /// <summary>
    /// The probe budget. It is intentionally shorter than the Compose
    /// healthcheck timeout so a hung dependency produces a fast 503 instead of
    /// blocking the healthcheck until Docker kills it.
    /// </summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(5);

    public async Task<ReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OverallTimeout);

        var checks = await Task.WhenAll(
            probes.Select(probe => CheckAsync(probe, deadline.Token)));

        return new ReadinessSnapshot(checks);
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
