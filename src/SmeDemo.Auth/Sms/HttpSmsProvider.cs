using System.Net.Http.Json;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Sms;

/// <summary>
/// Posts to an HTTP SMS gateway using the <c>{ to, text }</c> message shape from
/// requirements §6.1. Used only when <c>SMS_PROVIDER_URL</c> is configured.
/// </summary>
public sealed class HttpSmsProvider : ISmsProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _messagesEndpoint;
    private readonly ILogger<HttpSmsProvider> _logger;

    public HttpSmsProvider(HttpClient httpClient, AuthOptions options, ILogger<HttpSmsProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var baseUrl = options.SmsProviderUrl
            ?? throw new InvalidOperationException("SMS_PROVIDER_URL is required for the HTTP SMS provider.");
        var trimmed = baseUrl.TrimEnd('/');
        _messagesEndpoint = new Uri(
            trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}/messages",
            UriKind.Absolute);
    }

    public async Task<string> Send(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            _messagesEndpoint,
            new SmsMessage(phoneNumber, message),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var providerMessageId = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            providerMessageId = Guid.NewGuid().ToString();
        }

        _logger.LogInformation("SMS accepted by provider with id {ProviderMessageId}.", providerMessageId);
        return providerMessageId;
    }

    private sealed record SmsMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string To,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);
}
