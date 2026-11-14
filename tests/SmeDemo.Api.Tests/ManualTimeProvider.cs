namespace SmeDemo.Api.Tests;

/// <summary>
/// Controllable clock for the readiness cache. Advancing it expires cached
/// snapshots without a real delay.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
