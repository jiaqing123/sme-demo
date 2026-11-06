namespace SmeDemo.Auth.Errors;

/// <summary>
/// An auth failure rendered as a ProblemDetails document with the error code
/// contract from requirements §7.2 in its <c>error</c> extension.
/// </summary>
public sealed record ApiProblem(
    int StatusCode,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static ApiProblem Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.ValidationError, "One or more validation errors occurred.", errors);

    public static ApiProblem Unauthenticated(string message) =>
        new(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, message);

    public static ApiProblem RateLimited(string message) =>
        new(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, message);

    public static ApiProblem Internal() =>
        new(StatusCodes.Status500InternalServerError, ErrorCodes.Internal, "An unexpected error occurred.");
}
