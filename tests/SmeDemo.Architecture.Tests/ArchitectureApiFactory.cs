using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// Boots the real API composition root without touching external services. The
/// architecture tests only read endpoint metadata, so no HTTP call is made; the
/// values below satisfy <c>ApiOptions</c> and keep the seeder and issuer sync
/// off.
/// </summary>
internal sealed class ArchitectureApiFactory : WebApplicationFactory<SmeDemo.Api.Auth.CurrentUser>
{
    static ArchitectureApiFactory()
    {
        // ApiOptions reads IConfiguration, which includes process environment
        // variables. ConfigureAppConfiguration runs after the top-level Program
        // has already built its options in the minimal-hosting test host.
        SetEnvironmentVariable("DATABASE_URL", "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True");
        SetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING", "UseDevelopmentStorage=true");
        SetEnvironmentVariable("SEED_ENABLED", "false");
        SetEnvironmentVariable("AUTH_METADATA_URL", "http://localhost:1/.well-known/openid-configuration");
        SetEnvironmentVariable("AUTH_REQUIRE_HTTPS_METADATA", "false");
        SetEnvironmentVariable("JWT_ISSUER", "http://auth:8081");
        SetEnvironmentVariable("JWT_AUDIENCE", "sme-demo-api");
        SetEnvironmentVariable("ORG_ID", "sme");
        SetEnvironmentVariable("PUBLIC_BASE_URL", "http://localhost:8082");
        SetEnvironmentVariable("CORS_ORIGIN", "http://localhost:8080");
        SetEnvironmentVariable("ISSUER_SYNC_ENABLED", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "Server=unused;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
                ["AZURE_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                ["SEED_ENABLED"] = "false",
                ["AUTH_METADATA_URL"] = "http://localhost:1/.well-known/openid-configuration",
                ["AUTH_REQUIRE_HTTPS_METADATA"] = "false",
                ["JWT_ISSUER"] = "http://auth:8081",
                ["JWT_AUDIENCE"] = "sme-demo-api",
                ["ORG_ID"] = "sme",
                ["PUBLIC_BASE_URL"] = "http://localhost:8082",
                ["CORS_ORIGIN"] = "http://localhost:8080",
                ["ISSUER_SYNC_ENABLED"] = "false",
            });
        });
    }

    private static void SetEnvironmentVariable(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value);
}
