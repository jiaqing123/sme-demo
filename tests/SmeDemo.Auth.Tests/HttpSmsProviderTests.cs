using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Sms;

namespace SmeDemo.Auth.Tests;

/// <summary>
/// Pins the HTTP adapter's wire shape and its failure translation. Every test
/// runs against a stub <see cref="HttpMessageHandler"/>; no real network.
/// </summary>
[TestClass]
public sealed class HttpSmsProviderTests
{
    private const string Phone = "+61400000000";
    private const string Message = "Your SME Demo verification code is 123456. It expires in 5 minutes.";

    [TestMethod]
    [DataRow("http://sms-mock:8083")]
    [DataRow("http://sms-mock:8083/")]
    public async Task Send_PostsToMessagesEndpointWithSnakeCaseBody(string baseUrl)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(TextResponse(HttpStatusCode.Accepted, "sms-id")));
        var provider = CreateProvider(handler, baseUrl);

        await provider.Send(Phone, Message, CancellationToken.None);

        Assert.IsNotNull(handler.Request);
        Assert.AreEqual(HttpMethod.Post, handler.Request.Method);
        Assert.AreEqual("http://sms-mock:8083/messages", handler.Request.RequestUri?.AbsoluteUri);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(Phone, body.RootElement.GetProperty("to").GetString());
        Assert.AreEqual(Message, body.RootElement.GetProperty("text").GetString());
        Assert.IsFalse(body.RootElement.TryGetProperty("To", out _));
        Assert.IsFalse(body.RootElement.TryGetProperty("Text", out _));
    }

    [TestMethod]
    public async Task SuccessfulResponse_ReturnsProviderIdVerbatim()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(TextResponse(HttpStatusCode.Accepted, "sms-0123456789abcdef0123456789abcdef")));
        var provider = CreateProvider(handler, "http://sms-mock:8083");

        var providerMessageId = await provider.Send(Phone, Message, CancellationToken.None);

        Assert.AreEqual("sms-0123456789abcdef0123456789abcdef", providerMessageId);
    }

    [TestMethod]
    public async Task EmptyProviderId_FallsBackToAGuid()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(TextResponse(HttpStatusCode.Accepted, "")));
        var provider = CreateProvider(handler, "http://sms-mock:8083");

        var providerMessageId = await provider.Send(Phone, Message, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(providerMessageId));
        Assert.IsTrue(Guid.TryParse(providerMessageId, out _), $"'{providerMessageId}' is not a GUID.");
    }

    [TestMethod]
    public async Task NonSuccessStatus_ThrowsDeliveryException()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var provider = CreateProvider(handler, "http://sms-mock:8083");

        var exception = await Assert.ThrowsExactlyAsync<SmsDeliveryException>(
            async () => await provider.Send(Phone, Message, CancellationToken.None));

        StringAssert.Contains(exception.Message, "500");
    }

    [TestMethod]
    public async Task TransportFailure_IsWrappedInDeliveryException()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        var provider = CreateProvider(handler, "http://sms-mock:8083");

        var exception = await Assert.ThrowsExactlyAsync<SmsDeliveryException>(
            async () => await provider.Send(Phone, Message, CancellationToken.None));

        Assert.IsInstanceOfType<HttpRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task Timeout_IsWrappedInDeliveryException()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));
        var provider = CreateProvider(handler, "http://sms-mock:8083");

        await Assert.ThrowsExactlyAsync<SmsDeliveryException>(
            async () => await provider.Send(Phone, Message, CancellationToken.None));
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesUnwrapped()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("cancelled")));
        var provider = CreateProvider(handler, "http://sms-mock:8083");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await provider.Send(Phone, Message, cancellation.Token);
            Assert.Fail("A caller-cancelled send must throw OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            // Expected: the adapter must not translate caller cancellation into a 503.
        }
    }

    [TestMethod]
    public void MissingProviderUrl_ThrowsInvalidOperationException()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(TextResponse(HttpStatusCode.Accepted, "sms-id")));

        Assert.ThrowsExactly<InvalidOperationException>(() => CreateProvider(handler, null));
    }

    private static HttpSmsProvider CreateProvider(HttpMessageHandler handler, string? providerUrl)
    {
        var options = new AuthOptions(
            DatabaseUrl: "Server=unused;Database=sme_auth;User Id=unused;Password=unused;TrustServerCertificate=True",
            OrgId: "sme",
            DemoMode: true,
            DemoUsername: "demo",
            DemoPassword: "Demo_Passw0rd!",
            DemoPhone: Phone,
            DemoOtp: "123456",
            DemoOtpWasConfigured: false,
            JwtIssuer: "http://auth:8081",
            JwtAudience: "sme-demo-api",
            JwtTtl: TimeSpan.FromMinutes(30),
            CorsOrigin: "http://localhost:8080",
            KeysPath: "unused",
            SmsProviderUrl: providerUrl);

        return new HttpSmsProvider(
            new HttpClient(handler),
            options,
            NullLogger<HttpSmsProvider>.Instance);
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string content) =>
        new(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/plain"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }
}
