using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmsMock.Tests;

/// <summary>
/// Pins the sms-mock HTTP contract from requirements §6.5: the message envelope,
/// the text/plain provider id, and the validation error shape.
/// </summary>
[TestClass]
public sealed class SmsMockTests
{
    private static readonly Regex ProviderIdPattern = new("^sms-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

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
    [DataRow("/messages")]
    [DataRow("/messages/")]
    public async Task ValidMessage_ReturnsTextPlainProviderId(string path)
    {
        var response = await _client.PostAsync(path, Json("""
            { "to": "+61400000000", "text": "Your SME Demo verification code is 123456. It expires in 5 minutes." }
            """));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);

        var providerMessageId = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(
            ProviderIdPattern.IsMatch(providerMessageId),
            $"Provider message id '{providerMessageId}' does not match sms-<32 lowercase hex>.");
    }

    [TestMethod]
    [DataRow("to", """{ "text": "hello" }""")]
    [DataRow("to", """{ "to": "   ", "text": "hello" }""")]
    [DataRow("text", """{ "to": "+61400000000" }""")]
    [DataRow("text", """{ "to": "+61400000000", "text": "   " }""")]
    public async Task MissingOrBlankFields_ReturnValidationEnvelope(string field, string json)
    {
        await AssertValidationErrorAsync(field, json);
    }

    [TestMethod]
    public async Task ToLongerThan32Characters_ReturnsValidationError()
    {
        var json = JsonSerializer.Serialize(new
        {
            to = new string('1', 33),
            text = "hello",
        });

        await AssertValidationErrorAsync("to", json);
    }

    [TestMethod]
    public async Task TextLongerThan1600Characters_ReturnsValidationError()
    {
        var json = JsonSerializer.Serialize(new
        {
            to = "+61400000000",
            text = new string('x', 1601),
        });

        await AssertValidationErrorAsync("text", json);
    }

    [TestMethod]
    public async Task RequestBodyLargerThan64Kb_ReturnsBodyValidationError()
    {
        var json = JsonSerializer.Serialize(new
        {
            to = "+61400000000",
            text = new string('x', 70 * 1024),
        });

        await AssertValidationErrorAsync("body", json);
    }

    [TestMethod]
    public async Task BoundaryLengths_AreAccepted()
    {
        var response = await _client.PostAsync(
            "/messages",
            Json(JsonSerializer.Serialize(new
            {
                to = new string('1', 32),
                text = new string('x', 1600),
            })));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    [TestMethod]
    [DataRow("{ this is not json")]
    [DataRow("")]
    [DataRow("   ")]
    public async Task MalformedOrEmptyBody_ReturnsBodyValidationError(string body)
    {
        await AssertValidationErrorAsync("body", body);
    }

    [TestMethod]
    public async Task TwoRequests_ReturnDifferentProviderIds()
    {
        var first = await _client.PostAsync("/messages", Json("""{ "to": "+61400000000", "text": "first" }"""));
        var second = await _client.PostAsync("/messages", Json("""{ "to": "+61400000000", "text": "second" }"""));

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        var firstId = await first.Content.ReadAsStringAsync();
        var secondId = await second.Content.ReadAsStringAsync();

        Assert.IsTrue(ProviderIdPattern.IsMatch(firstId));
        Assert.IsTrue(ProviderIdPattern.IsMatch(secondId));
        Assert.AreNotEqual(firstId, secondId);
    }

    private async Task AssertValidationErrorAsync(string field, string json)
    {
        var response = await _client.PostAsync("/messages", Json(json));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.AreEqual(
            "One or more validation errors occurred.",
            error.GetProperty("message").GetString());

        var errors = error.GetProperty("errors");
        Assert.IsTrue(
            errors.TryGetProperty(field, out var messages),
            $"Expected a validation error for '{field}'.");
        Assert.IsTrue(messages.GetArrayLength() > 0, $"The '{field}' error list is empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(messages[0].GetString()));
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");
}
