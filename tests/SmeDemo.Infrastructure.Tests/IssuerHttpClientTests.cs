using System.Net;
using System.Text;
using SmeDemo.Infrastructure.Issuer;

namespace SmeDemo.Infrastructure.Tests;

/// <summary>
/// The issuer adapter is the trust boundary for an external HTTP dependency, so
/// these tests pin the mapping and the rejection of a response that belongs to a
/// different card.
/// </summary>
[TestClass]
public sealed class IssuerHttpClientTests
{
    private static readonly Guid CardId = Guid.Parse("c1000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherCardId = Guid.Parse("c1000000-0000-4000-8000-000000000002");

    [TestMethod]
    public async Task GetTransactions_MapsTheDocumentedShape()
    {
        var client = CreateClient(HttpStatusCode.OK, ValidBody(CardId));

        var transactions = await client.GetTransactionsAsync(CardId, CancellationToken.None);

        Assert.AreEqual(1, transactions.Count);
        var transaction = transactions[0];
        Assert.AreEqual(CardId, transaction.CardId);
        Assert.AreEqual(Guid.Parse("00000000-0000-4000-8000-000000000001"), transaction.TransactionId);
        Assert.AreEqual(450, transaction.AmountMinor);
        Assert.AreEqual("AUD", transaction.Currency);
        Assert.AreEqual("debit", transaction.Direction);
        Assert.AreEqual("Dan Murphy's", transaction.Merchant);
        Assert.AreEqual("posted", transaction.Status);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero),
            transaction.OccurredAt);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero),
            transaction.PostedAt);
        StringAssert.Contains(transaction.RawPayload, "\"transaction_id\"");
    }

    [TestMethod]
    public async Task GetTransactions_ResponseForAnotherCard_IsRejected()
    {
        var client = CreateClient(HttpStatusCode.OK, ValidBody(OtherCardId));

        await Assert.ThrowsExactlyAsync<InvalidIssuerPayloadException>(() =>
            client.GetTransactionsAsync(CardId, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetTransactions_MissingArray_IsRejected()
    {
        var client = CreateClient(
            HttpStatusCode.OK,
            $$"""{"card_id":"{{CardId}}"}""");

        await Assert.ThrowsExactlyAsync<InvalidIssuerPayloadException>(() =>
            client.GetTransactionsAsync(CardId, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetTransactions_NonSuccessStatus_ReportsStatusWithoutBody()
    {
        var client = CreateClient(HttpStatusCode.ServiceUnavailable, "upstream detail");

        var exception = await Assert.ThrowsExactlyAsync<IssuerRequestException>(() =>
            client.GetTransactionsAsync(CardId, CancellationToken.None));

        Assert.AreEqual("The issuer returned HTTP 503.", exception.Message);
        Assert.IsFalse(exception.Message.Contains("upstream detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetTransactions_TimestampWithoutOffset_IsTreatedAsUtc()
    {
        var client = CreateClient(
            HttpStatusCode.OK,
            BodyWithTimes("2026-10-31T21:00:00", "2026-10-31T23:00:00"));

        var transactions = await client.GetTransactionsAsync(CardId, CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, transactions[0].OccurredAt.Offset);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero),
            transactions[0].OccurredAt);
    }

    [TestMethod]
    public async Task GetTransactions_OffsetTimestamp_IsNormalisedToUtc()
    {
        var client = CreateClient(
            HttpStatusCode.OK,
            BodyWithTimes("2026-11-01T08:00:00+11:00", "2026-11-01T10:00:00+11:00"));

        var transactions = await client.GetTransactionsAsync(CardId, CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, transactions[0].OccurredAt.Offset);
        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero),
            transactions[0].OccurredAt);
    }

    [TestMethod]
    public async Task GetTransactions_MoreThanFiveHundred_IsRejected()
    {
        var transactions = string.Join(
            ",",
            Enumerable.Range(1, 501).Select(sequence =>
                $$"""
                {
                  "transaction_id": "00000000-0000-4000-8000-{{sequence:D12}}",
                  "amount_minor": 100,
                  "currency": "AUD",
                  "direction": "debit",
                  "merchant": "Shop",
                  "status": "posted",
                  "occurred_at": "2026-10-31T21:00:00Z",
                  "posted_at": "2026-10-31T23:00:00Z"
                }
                """));

        var client = CreateClient(
            HttpStatusCode.OK,
            $$"""{"card_id":"{{CardId}}","transactions":[{{transactions}}]}""");

        await Assert.ThrowsExactlyAsync<InvalidIssuerPayloadException>(() =>
            client.GetTransactionsAsync(CardId, CancellationToken.None));
    }

    private static IssuerHttpClient CreateClient(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("http://issuer-mock:8084/"),
        };
        return new IssuerHttpClient(http);
    }

    private static string ValidBody(Guid cardId) =>
        $$"""
        {
          "card_id": "{{cardId}}",
          "transactions": [
            {
              "transaction_id": "00000000-0000-4000-8000-000000000001",
              "amount_minor": 450,
              "currency": "AUD",
              "direction": "debit",
              "merchant": "Dan Murphy's",
              "status": "posted",
              "occurred_at": "2026-10-31T21:00:00Z",
              "posted_at": "2026-10-31T23:00:00Z"
            }
          ]
        }
        """;

    private static string BodyWithTimes(string occurredAt, string postedAt) =>
        $$"""
        {
          "card_id": "{{CardId}}",
          "transactions": [
            {
              "transaction_id": "00000000-0000-4000-8000-000000000001",
              "amount_minor": 450,
              "currency": "AUD",
              "direction": "debit",
              "merchant": "Dan Murphy's",
              "status": "posted",
              "occurred_at": "{{occurredAt}}",
              "posted_at": "{{postedAt}}"
            }
          ]
        }
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
