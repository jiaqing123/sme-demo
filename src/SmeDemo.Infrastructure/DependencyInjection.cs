using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Application.Abstractions;
using SmeDemo.Infrastructure.Issuer;
using SmeDemo.Infrastructure.Persistence;
using SmeDemo.Infrastructure.Storage;

namespace SmeDemo.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The only API-side file besides <c>Program.cs</c> that may reference EF Core
    /// or Azure SDK client types. Endpoints depend on the ports declared in the
    /// application layer.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddDbContext<AppDbContext>(db => db.UseSqlServer(options.DatabaseUrl));

        services.AddSingleton(options);
        services.AddSingleton(_ => new TableServiceClient(options.StorageConnectionString));
        services.AddSingleton(provider =>
            provider.GetRequiredService<TableServiceClient>().GetTableClient(options.TransactionsTable));
        services.AddSingleton(_ => new BlobServiceClient(options.StorageConnectionString));
        services.AddSingleton(provider =>
            provider.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(options.CardArtContainer));

        services.AddScoped<ICardRepository, EfCardRepository>();
        services.AddSingleton<ITransactionStore, TableTransactionStore>();
        services.AddSingleton<IBlobStore, BlobCardArtStore>();

        if (options.IssuerSyncEnabled)
        {
            services.AddHttpClient<IIssuerClient, IssuerHttpClient>(client =>
            {
                // The mock issuer is a plain-HTTP internal dependency. The API
                // key is a demo value supplied by Compose and never logged.
                client.BaseAddress = new Uri(options.IssuerBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("X-Api-Key", options.IssuerApiKey);
            });
        }

        return services;
    }
}
