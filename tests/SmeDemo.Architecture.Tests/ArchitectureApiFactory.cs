using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// Boots the real API composition root without touching external services. The
/// architecture tests only read endpoint metadata, so no HTTP call is made; the
/// per-factory settings below satisfy <c>ApiOptions</c> and keep the seeder and
/// issuer sync off.
/// </summary>
internal sealed class ArchitectureApiFactory : WebApplicationFactory<SmeDemo.Api.Auth.CurrentUser>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Host settings reach builder.Configuration before the top-level Program
        // reads ApiOptions, so no process-wide environment mutation is needed.
        builder.UseSetting("DATABASE_URL", "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True");
        builder.UseSetting("AZURE_STORAGE_CONNECTION_STRING", "UseDevelopmentStorage=true");
        builder.UseSetting("SEED_ENABLED", "false");
        builder.UseSetting("AUTH_METADATA_URL", "http://localhost:1/.well-known/openid-configuration");
        builder.UseSetting("AUTH_REQUIRE_HTTPS_METADATA", "false");
        builder.UseSetting("JWT_ISSUER", "http://auth:8081");
        builder.UseSetting("JWT_AUDIENCE", "sme-demo-api");
        builder.UseSetting("ORG_ID", "sme");
        builder.UseSetting("PUBLIC_BASE_URL", "http://localhost:8082");
        builder.UseSetting("CORS_ORIGIN", "http://localhost:8080");
        builder.UseSetting("ISSUER_SYNC_ENABLED", "false");
    }
}
