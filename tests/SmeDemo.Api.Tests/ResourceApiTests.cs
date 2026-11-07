using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SmeDemo.Api.Tests;

[TestClass]
public sealed class ResourceApiTests
{
    private ApiTestFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherCardId = Guid.Parse("c1000000-0000-4000-8000-000000000099");

    [TestInitialize]
    public void Initialize()
    {
        _factory = new ApiTestFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Healthz_IsAnonymous()
    {
        var response = await _client.GetAsync("/healthz");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", body.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Cards_WithoutToken_ReturnsUnauthenticated()
    {
        var response = await _client.GetAsync("/api/v1/me/cards");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(
            "UNAUTHENTICATED",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Cards_WithExpiredToken_ReturnsTokenExpired()
    {
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-60);
        _client.DefaultRequestHeaders.Authorization = Bearer(
            _factory.CreateToken(issuedAt: issuedAt, expiresAt: issuedAt.AddMinutes(30)));

        var response = await _client.GetAsync("/api/v1/me/cards");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(
            "TOKEN_EXPIRED",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Me_ReturnsTokenClaimsOnly_WithSnakeCaseNames()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync("/api/v1/me");
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(ApiTestFactory.UserId.ToString(), root.GetProperty("id").GetString());
        Assert.AreEqual("sme", root.GetProperty("org").GetString());
        Assert.AreEqual("Alex Morgan", root.GetProperty("name").GetString());
        Assert.IsFalse(root.TryGetProperty("phone", out _));
    }

    [TestMethod]
    public async Task Cards_ReturnThreeCardsInDisplayOrder_WithSnakeCaseFields()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync("/api/v1/me/cards");
        using var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, items.Length);
        CollectionAssert.AreEqual(
            new[] { "4242", "5454", "1881" },
            items.Select(item => item.GetProperty("last4").GetString()).ToArray());
        Assert.AreEqual(0, items[0].GetProperty("display_order").GetInt32());
        Assert.AreEqual(9, items[0].GetProperty("expiry_month").GetInt32());
        Assert.AreEqual(2028, items[0].GetProperty("expiry_year").GetInt32());
        Assert.AreEqual("AUD", items[0].GetProperty("currency").GetString());
        Assert.AreEqual("active", items[0].GetProperty("status").GetString());
        StringAssert.EndsWith(
            items[0].GetProperty("art_url").GetString(),
            $"/api/v1/cards/{FirstCardId}/art");
    }

    [TestMethod]
    public async Task Cards_WithWrongOrg_ReturnsForbidden()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken(org: "other"));

        var response = await _client.GetAsync("/api/v1/me/cards");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(
            "FORBIDDEN",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Transactions_UnknownCard_ReturnsNotFound()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync($"/api/v1/me/cards/{Guid.NewGuid()}/transactions");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "NOT_FOUND",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Transactions_OtherUsersCard_ReturnsForbidden()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync($"/api/v1/me/cards/{OtherCardId}/transactions");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(
            "FORBIDDEN",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Transactions_PaginateWithoutDuplicates_AndUseSnakeCase()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var firstResponse = await _client.GetAsync(
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=3");
        using var firstBody = await ReadJsonAsync(firstResponse);
        var firstItems = firstBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var nextCursor = firstBody.RootElement.GetProperty("next_cursor").GetString();

        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.AreEqual(3, firstItems.Length);
        Assert.IsNotNull(nextCursor);
        Assert.IsTrue(firstItems[0].TryGetProperty("amount_minor", out _));
        Assert.IsTrue(firstItems[0].TryGetProperty("card_id", out _));
        Assert.IsTrue(firstItems[0].TryGetProperty("occurred_at", out _));

        var secondResponse = await _client.GetAsync(
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=3&cursor={Uri.EscapeDataString(nextCursor)}");
        using var secondBody = await ReadJsonAsync(secondResponse);
        var secondItems = secondBody.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual(3, secondItems.Length);
        Assert.IsFalse(firstItems
            .Select(item => item.GetProperty("id").GetString())
            .Intersect(secondItems.Select(item => item.GetProperty("id").GetString()))
            .Any());
    }

    [TestMethod]
    public async Task Transactions_InvalidLimit_ReturnsValidationError()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync(
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=101");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = body.RootElement.GetProperty("error");
        Assert.AreEqual("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.IsTrue(error.GetProperty("errors").TryGetProperty("limit", out _));
    }

    [TestMethod]
    public async Task CardArt_StreamsSvg_WithContentTypeAndCacheControl()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync($"/api/v1/cards/{FirstCardId}/art");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        // Ordering is not part of the contract; assert the semantics.
        Assert.IsTrue(response.Headers.CacheControl?.Private);
        Assert.AreEqual(TimeSpan.FromHours(1), response.Headers.CacheControl?.MaxAge);
    }

    [TestMethod]
    public async Task Transactions_InvalidCursor_ReturnsValidationError()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync(
            $"/api/v1/me/cards/{FirstCardId}/transactions?cursor=not-a-cursor");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = body.RootElement.GetProperty("error");
        Assert.AreEqual("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.IsTrue(error.GetProperty("errors").TryGetProperty("cursor", out _));
    }

    [TestMethod]
    public async Task Transactions_LastPage_HasNullNextCursor()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync(
            $"/api/v1/me/cards/{FirstCardId}/transactions?limit=100");
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(8, root.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("next_cursor").ValueKind);
    }

    [TestMethod]
    public async Task CardArt_OtherUsersCard_ReturnsForbidden()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync($"/api/v1/cards/{OtherCardId}/art");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(
            "FORBIDDEN",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task UnknownRoute_ReturnsNotFoundEnvelope()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "NOT_FOUND",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task NonGuidCardId_ReturnsNotFoundEnvelope()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());

        var response = await _client.GetAsync("/api/v1/me/cards/not-a-guid/transactions");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "NOT_FOUND",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Transactions_WhenStoreIsUnavailable_ReturnsUpstreamUnavailable()
    {
        _client.DefaultRequestHeaders.Authorization = Bearer(_factory.CreateToken());
        _factory.Transactions.Unavailable = true;

        var response = await _client.GetAsync($"/api/v1/me/cards/{FirstCardId}/transactions");
        using var body = await ReadJsonAsync(response);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(
            "UPSTREAM_UNAVAILABLE",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
    }

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
