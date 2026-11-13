namespace SmeDemo.Auth.Errors;

public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string NotFound = "NOT_FOUND";

    /// <summary>
    /// A matched path was called with an unsupported HTTP method. This is a 405
    /// routing failure, not an authentication failure.
    /// </summary>
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";

    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RateLimited = "RATE_LIMITED";
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";
    public const string Internal = "INTERNAL";
}
