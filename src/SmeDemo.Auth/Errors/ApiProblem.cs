namespace SmeDemo.Auth.Errors;

/// <summary>
/// An auth failure rendered as a ProblemDetails document with the error code
/// contract from requirements §7.2 in its <c>error</c> extension.
/// </summary>
public sealed record ApiProblem(
    int StatusCode,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    TimeSpan? RetryAfter = null)
{
    public static ApiProblem Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "One or more validation errors occurred.", errors);

    public static ApiProblem Unauthenticated(string message) =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, message);

    public static ApiProblem TokenExpired() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.TokenExpired, "Access token has expired.");

    public static ApiProblem InvalidToken() =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidToken, "The access token is not valid.");

    public static ApiProblem NotFound(string message) =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);

    /// <summary>
    /// A routing method mismatch. This is a 405, not an authentication failure:
    /// the path exists but does not support the requested verb.
    /// </summary>
    public static ApiProblem MethodNotAllowed() =>
        new(
            StatusCodes.Status405MethodNotAllowed,
            ErrorCodes.MethodNotAllowed,
            "The HTTP method is not supported for this resource.");

    public static ApiProblem RateLimited(string message, TimeSpan? retryAfter = null) =>
        new(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, message, RetryAfter: retryAfter);

    public static ApiProblem UpstreamUnavailable() =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.UpstreamUnavailable,
            "The SMS provider is unavailable. Try again shortly.",
            RetryAfter: TimeSpan.FromSeconds(5));

    public static ApiProblem PayloadTooLarge() =>
        new(
            StatusCodes.Status413PayloadTooLarge,
            ErrorCodes.PayloadTooLarge,
            "The request body is too large.");

    public static ApiProblem UnsupportedMediaType() =>
        new(
            StatusCodes.Status415UnsupportedMediaType,
            ErrorCodes.UnsupportedMediaType,
            "The request content type is not supported.");

    public static ApiProblem Internal() =>
        new(StatusCodes.Status500InternalServerError, ErrorCodes.Internal, "An unexpected error occurred.");
}
