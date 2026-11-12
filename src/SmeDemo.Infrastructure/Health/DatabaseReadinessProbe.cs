using SmeDemo.Application.Abstractions;
using SmeDemo.Infrastructure.Persistence;

namespace SmeDemo.Infrastructure.Health;

/// <summary>
/// Proves the API's relational store is reachable. The check is scoped because
/// it borrows the request-scoped <see cref="AppDbContext"/>; EF Core's
/// <c>CanConnectAsync</c> opens (and closes) the underlying connection.
/// </summary>
public sealed class DatabaseReadinessProbe(AppDbContext db) : IReadinessProbe
{
    public string Name => "database";

    public Task<bool> CheckAsync(CancellationToken cancellationToken) =>
        db.Database.CanConnectAsync(cancellationToken);
}
