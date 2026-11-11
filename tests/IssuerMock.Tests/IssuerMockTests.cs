using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text.Json;

namespace IssuerMock.Tests;

[TestClass]
public sealed class IssuerMockTests
{
    private const string ApiKey = "test-issuer-key";

    private static readonly Guid FirstCardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    private static readonly Guid SecondCardId = Guid.Parse("c1000000-0000-4000-8000-000000000002");
    private static readonly Guid ThirdCardId = Guid.Parse("c1000000-0000-4000-8000-000000000003");

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    static IssuerMockTests()
    {
        Environment.SetEnvironmentVariable("ISSUER_API_KEY", ApiKey);
    }

    [TestInitialize]
    public void Initialize()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Healthz_ReturnsOk()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("ok", body.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task ThreeCards_ReturnFortyTransactions_NewestFirst()
    {
        var counts = new List<int>();
        var firstIds = new List<string?>();

        foreach (var cardId in new[] { FirstCardId, SecondCardId, ThirdCardId })
        {
            using var body = await GetTransactionsAsync(cardId);
            var transactions = body.RootElement.GetProperty("transactions").EnumerateArray().ToArray();
            counts.Add(transactions.Length);
            firstIds.Add(transactions[0].GetProperty("transaction_id").GetString());

            var times = transactions
                .Select(transaction => transaction.GetProperty("occurred_at").GetDateTimeOffset())
                .ToArray();
            CollectionAssert.AreEqual(
                times.OrderByDescending(time => time).ToArray(),
                times,
                "Transactions must be returned newest first.");
        }

        CollectionAssert.AreEqual(new[] { 14, 13, 13 }, counts);
        Assert.AreEqual(40, counts.Sum());
        CollectionAssert.AreEqual(
            new[]
            {
                "00000000-0000-4000-8000-000000000001",
                "00000000-0000-4000-8000-000000000015",
                "00000000-0000-4000-8000-000000000028",
            },
            firstIds);
    }

    [TestMethod]
    public async Task FixtureContract_IsStable()
    {
        using var body = await GetTransactionsAsync(FirstCardId);
        var transactions = body.RootElement.GetProperty("transactions").EnumerateArray().ToArray();

        Assert.AreEqual("c1000000-0000-4000-8000-000000000001", body.RootElement.GetProperty("card_id").GetString());
        Assert.AreEqual(14, transactions.Length);

        var first = transactions[0];
        Assert.AreEqual(450, first.GetProperty("amount_minor").GetInt64());
        Assert.AreEqual("AUD", first.GetProperty("currency").GetString());
        Assert.AreEqual("debit", first.GetProperty("direction").GetString());
        Assert.AreEqual("Dan Murphy's", first.GetProperty("merchant").GetString());
        Assert.AreEqual("posted", first.GetProperty("status").GetString());
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero),
            first.GetProperty("occurred_at").GetDateTimeOffset());
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero),
            first.GetProperty("posted_at").GetDateTimeOffset());

        // Every transaction field is present under its snake_case name.
        foreach (var transaction in transactions)
        {
            Assert.IsTrue(transaction.TryGetProperty("transaction_id", out _));
            Assert.IsTrue(transaction.TryGetProperty("amount_minor", out _));
            Assert.IsTrue(transaction.TryGetProperty("currency", out _));
            Assert.IsTrue(transaction.TryGetProperty("direction", out _));
            Assert.IsTrue(transaction.TryGetProperty("merchant", out _));
            Assert.IsTrue(transaction.TryGetProperty("status", out _));
            Assert.IsTrue(transaction.TryGetProperty("occurred_at", out _));
            Assert.IsTrue(transaction.TryGetProperty("posted_at", out _));
        }
    }

    [TestMethod]
    public async Task RepeatedRequests_AreByteIdentical()
    {
        var first = await _client.SendAsync(AuthorizedRequest(FirstCardId));
        var second = await _client.SendAsync(AuthorizedRequest(FirstCardId));

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        CollectionAssert.AreEqual(
            await first.Content.ReadAsByteArrayAsync(),
            await second.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task Fixture_MatchesTheSeedContractForEveryTransaction()
    {
        var merchants = new[]
        {
            "Woolworths", "Coles", "JB Hi-Fi", "Bunnings", "Uber",
            "Qantas", "Officeworks", "Bp", "Netflix", "Dan Murphy's",
        };
        var cards = new[]
        {
            (Id: FirstCardId, Count: 14, FirstOffsetHours: -3, SpacingHours: 3),
            (Id: SecondCardId, Count: 13, FirstOffsetHours: -2, SpacingHours: 5),
            (Id: ThirdCardId, Count: 13, FirstOffsetHours: -1, SpacingHours: 4),
        };
        var baseTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var sequence = 0;

        foreach (var card in cards)
        {
            using var body = await GetTransactionsAsync(card.Id);
            var transactions = body.RootElement.GetProperty("transactions").EnumerateArray().ToArray();
            Assert.AreEqual(card.Count, transactions.Length);

            for (var index = 0; index < transactions.Length; index++)
            {
                sequence++;
                var occurredAt = baseTime.AddHours(
                    card.FirstOffsetHours - (index * card.SpacingHours));
                var isCredit = index % 7 == 6;
                var transaction = transactions[index];

                Assert.AreEqual(
                    $"00000000-0000-4000-8000-{sequence:D12}",
                    transaction.GetProperty("transaction_id").GetString());
                Assert.AreEqual(
                    isCredit ? 5000 + (index * 1375) : 450 + (index * 1031),
                    transaction.GetProperty("amount_minor").GetInt64());
                Assert.AreEqual("AUD", transaction.GetProperty("currency").GetString());
                Assert.AreEqual(
                    isCredit ? "credit" : "debit",
                    transaction.GetProperty("direction").GetString());
                Assert.AreEqual(
                    merchants[(index + 99) % 10],
                    transaction.GetProperty("merchant").GetString());
                Assert.AreEqual(
                    index % 9 == 8 ? "pending" : "posted",
                    transaction.GetProperty("status").GetString());
                Assert.AreEqual(occurredAt, transaction.GetProperty("occurred_at").GetDateTimeOffset());
                Assert.AreEqual(occurredAt.AddHours(2), transaction.GetProperty("posted_at").GetDateTimeOffset());
            }
        }

        Assert.AreEqual(40, sequence);
    }

    [TestMethod]
    public async Task MissingKey_ReturnsUnauthorizedEnvelope()
    {
        var response = await _client.GetAsync($"/issuer/v1/cards/{FirstCardId}/transactions");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(
            "UNAUTHORIZED",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task WrongKey_ReturnsUnauthorizedEnvelope()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/issuer/v1/cards/{FirstCardId}/transactions");
        request.Headers.Add("X-Api-Key", "wrong-key");

        var response = await _client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(
            "UNAUTHORIZED",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task UnknownCard_ReturnsNotFoundEnvelope()
    {
        var response = await _client.SendAsync(AuthorizedRequest(Guid.NewGuid()));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "CARD_NOT_FOUND",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<JsonDocument> GetTransactionsAsync(Guid cardId)
    {
        var response = await _client.SendAsync(AuthorizedRequest(cardId));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage AuthorizedRequest(Guid cardId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/issuer/v1/cards/{cardId}/transactions");
        request.Headers.Add("X-Api-Key", ApiKey);
        return request;
    }
}
