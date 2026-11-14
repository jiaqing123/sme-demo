using System.Net;
using System.Text.RegularExpressions;

namespace SmeDemo.Api.Tests;

/// <summary>
/// PR 3 correlation contract: every API response, including the shared error
/// envelopes, carries a safe <c>X-Request-Id</c> that the logs can be joined on.
/// </summary>
[TestClass]
public sealed class RequestIdTests
{
    private const string HeaderName = "X-Request-Id";

    private static readonly Regex GeneratedId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    [TestMethod]
    public async Task GeneratedRequestId_IsPresentOnHealth()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var requestId = ReadRequestId(response);
        StringAssert.Matches(
            requestId,
            GeneratedId,
            $"A generated id must be the 32-char trace id (or Guid) form, was '{requestId}'.");
    }

    [TestMethod]
    public async Task ValidInboundRequestId_IsEchoed()
    {
        const string inbound = "client-request_42:abc.def";

        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.TryAddWithoutValidation(HeaderName, inbound);

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(inbound, ReadRequestId(response));
    }

    [TestMethod]
    public async Task InvalidInboundRequestIds_AreReplaced()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        var invalidIds = new[]
        {
            new string('a', 65),
            "contains a space",
            string.Empty,
        };

        foreach (var invalid in invalidIds)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.TryAddWithoutValidation(HeaderName, invalid);

            using var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var requestId = ReadRequestId(response);
            Assert.AreNotEqual(invalid, requestId, $"The unsafe inbound value '{invalid}' must not be echoed.");
            StringAssert.Matches(requestId, GeneratedId, $"Invalid input must be replaced by a generated id, was '{requestId}'.");
        }
    }

    [TestMethod]
    public async Task ErrorResponses_CarryRequestId()
    {
        using var factory = new ApiTestFactory();
        using var client = factory.CreateClient();

        using var unauthorized = await client.GetAsync("/api/v1/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        StringAssert.Matches(ReadRequestId(unauthorized), GeneratedId, "A 401 must carry a request id.");

        using var notFound = await client.GetAsync("/api/v1/does-not-exist");
        Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
        StringAssert.Matches(ReadRequestId(notFound), GeneratedId, "A 404 must carry a request id.");
    }

    [TestMethod]
    public async Task JsonLogFormat_HostStartsAndServesARequest()
    {
        using var defaultFactory = new ApiTestFactory();
        using var jsonFactory = defaultFactory.WithWebHostBuilder(
            builder => builder.UseSetting("LOG_FORMAT", "json"));
        using var client = jsonFactory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsFalse(string.IsNullOrWhiteSpace(ReadRequestId(response)));
    }

    private static string ReadRequestId(HttpResponseMessage response)
    {
        Assert.IsTrue(
            response.Headers.TryGetValues(HeaderName, out var values),
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: the response must carry {HeaderName}.");
        var value = values!.Single();

        Assert.IsFalse(string.IsNullOrWhiteSpace(value), $"{HeaderName} must not be blank.");
        return value;
    }
}
