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
    string OrgId,
    string IssuerBaseUrl,
    string IssuerApiKey,
    TimeSpan IssuerSyncInterval,
    bool IssuerSyncEnabled)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(StorageConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(TransactionsTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(CardArtContainer);
        ArgumentException.ThrowIfNullOrWhiteSpace(OrgId);

        if (IssuerSyncInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(IssuerSyncInterval),
                "The issuer sync interval must be positive.");
        }

        if (!IssuerSyncEnabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(IssuerBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(IssuerApiKey);

        if (!Uri.TryCreate(IssuerBaseUrl, UriKind.Absolute, out var issuerUri) ||
            (issuerUri.Scheme != Uri.UriSchemeHttp && issuerUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "IssuerBaseUrl must be an absolute http(s) URL.",
                nameof(IssuerBaseUrl));
        }
    }
}
