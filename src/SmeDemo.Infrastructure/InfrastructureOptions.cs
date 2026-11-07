using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Application.Abstractions;
using SmeDemo.Infrastructure.Persistence;
using SmeDemo.Infrastructure.Storage;

namespace SmeDemo.Infrastructure;

/// <summary>
/// Runtime configuration for the adapters. Only <see cref="DependencyInjection"/>
/// and the API composition root deal with these values.
/// </summary>
public sealed record InfrastructureOptions(
    string DatabaseUrl,
    string StorageConnectionString,
    string TransactionsTable,
    string CardArtContainer,
    string OrgId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(StorageConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(TransactionsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(CardArtContainer);
        ArgumentException.ThrowIfNullOrWhiteSpace(OrgId);
    }
}
