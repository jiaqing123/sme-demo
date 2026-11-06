namespace SmeDemo.Auth.Sms;

/// <summary>
/// Demo fallback: logs the message instead of making a network call. The phone
/// number is masked. This implementation is only reachable when DEMO_MODE is
/// true; non-demo start-up requires a configured SMS provider.
/// </summary>
public sealed class LoggingSmsProvider : ISmsProvider
{
    private readonly ILogger<LoggingSmsProvider> _logger;

    public LoggingSmsProvider(ILogger<LoggingSmsProvider> logger) => _logger = logger;

    public Task<string> Send(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "SMS (logged, no provider configured) to {MaskedPhone}: {Message}",
            Mask(phoneNumber),
            message);

        return Task.FromResult($"log-{Guid.NewGuid():N}");
    }

    private static string Mask(string phoneNumber) =>
        phoneNumber.Length <= 4 ? "****" : $"****{phoneNumber[^4..]}";
}
