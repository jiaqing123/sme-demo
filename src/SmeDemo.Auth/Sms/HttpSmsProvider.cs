using System.Net.Http.Json;
using SmeDemo.Auth.Configuration;

namespace SmeDemo.Auth.Sms;

/// <summary>
/// Posts to an HTTP SMS gateway using the <c>{ to, text }</c> message shape from
/// requirements §6.1. Used only when <c>SMS_PROVIDER_URL</c> is configured.
/// Transport failures are translated into <see cref="SmsDeliveryException"/> so
/// callers can render a readable 503 instead of a 500.
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
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _messagesEndpoint,
                new SmsMessage(phoneNumber, message),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // The status code is a safe diagnostic; the body is not logged and
                // not carried on the exception.
                _logger.LogWarning(
                    "SMS provider returned HTTP {StatusCode}.",
                    (int)response.StatusCode);
                throw new SmsDeliveryException(
                    $"The SMS provider returned HTTP {(int)response.StatusCode}.");
            }

            var providerMessageId = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(providerMessageId))
            {
                providerMessageId = Guid.NewGuid().ToString();
            }

            _logger.LogInformation("SMS accepted by provider with id {ProviderMessageId}.", providerMessageId);
            return providerMessageId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation that the caller did not
            // request. A caller-requested cancellation is rethrown below untouched.
            _logger.LogWarning("SMS provider request timed out.");
            throw new SmsDeliveryException("The SMS provider request timed out.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning("SMS provider request failed.");
            throw new SmsDeliveryException("The SMS provider request failed.", exception);
        }
    }

    private sealed record SmsMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string To,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);
}
