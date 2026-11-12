namespace SmeDemo.Application.Abstractions;

/// <summary>
/// One dependency the API needs before it can serve traffic. Readiness probes
/// are deliberately tiny and exception-safe: the orchestrator turns a thrown
/// exception, a timeout, or a <c>false</c> result into a degraded check without
/// exposing the failure detail to the caller.
/// </summary>
public interface IReadinessProbe
{
    /// <summary>The stable name reported in the readiness document.</summary>
    string Name { get; }

    /// <summary>
    /// Returns <c>true</c> when the dependency is reachable and usable. The
    /// implementation should let transport failures propagate and let the
    /// orchestrator report them as degraded, rather than logging credentials.
    /// </summary>
    Task<bool> CheckAsync(CancellationToken cancellationToken);
}
