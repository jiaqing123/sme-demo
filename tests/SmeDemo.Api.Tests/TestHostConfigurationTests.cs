using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SmeDemo.Api.Tests;

/// <summary>
/// Proves the test host supplies configuration per factory rather than through
/// process-wide environment variables: two hosts with different settings must
/// resolve their own <c>ApiOptions</c> and validate their own JWT audience.
/// </summary>
[TestClass]
public sealed class TestHostConfigurationTests
{
    [TestMethod]
    public void TwoFactories_WithDifferentConfiguration_ResolveTheirOwnOptions()
    {
        using var first = new ApiTestFactory(
            databaseUrl: "Server=first-host;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
            jwtAudience: "sme-demo-api-first");
        using var second = new ApiTestFactory(
            databaseUrl: "Server=second-host;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
            jwtAudience: "sme-demo-api-second");

        Assert.AreEqual("sme-demo-api-first", first.Options.JwtAudience);
        Assert.AreEqual("sme-demo-api-second", second.Options.JwtAudience);
        Assert.AreEqual(
            "Server=first-host;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
            first.Options.DatabaseUrl);
        Assert.AreEqual(
            "Server=second-host;Database=sme_api;User Id=unused;Password=unused;TrustServerCertificate=True",
            second.Options.DatabaseUrl);
    }

    [TestMethod]
    public async Task TokenMintedForOneFactoryAudience_IsRejectedByTheOther()
    {
        using var first = new ApiTestFactory(jwtAudience: "sme-demo-api-first");
        using var second = new ApiTestFactory(jwtAudience: "sme-demo-api-second");

        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();

        firstClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", first.CreateToken());
        var accepted = await firstClient.GetAsync("/api/v1/me");
        Assert.AreEqual(
            HttpStatusCode.OK,
            accepted.StatusCode,
            "The token minted for the first factory must be accepted by that factory.");

        secondClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", first.CreateToken());
        var rejected = await secondClient.GetAsync("/api/v1/me");

        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            rejected.StatusCode,
            "A token minted for the first factory's audience must be rejected by the second factory.");

        using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "INVALID_TOKEN",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
