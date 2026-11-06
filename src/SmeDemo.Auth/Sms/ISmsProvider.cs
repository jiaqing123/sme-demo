namespace SmeDemo.Auth.Sms;

/// <summary>
/// The only seam to an SMS gateway. Swapping the demo implementation for a real
/// provider means one adapter, not a rewrite.
/// </summary>
public interface ISmsProvider
{
    Task<string> Send(string phoneNumber, string message, CancellationToken cancellationToken);
}
