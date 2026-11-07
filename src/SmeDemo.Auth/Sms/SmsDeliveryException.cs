namespace SmeDemo.Auth.Sms;

/// <summary>
/// The gateway could not deliver the message: it was unreachable, timed out, or
/// returned a non-2xx status. The response body is deliberately not carried, so
/// provider diagnostics cannot leak into the auth error surface or logs.
/// </summary>
public sealed class SmsDeliveryException : Exception
{
    public SmsDeliveryException(string message)
        : base(message)
    {
    }

    public SmsDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
