using Microsoft.AspNetCore.Diagnostics;

namespace SmeDemo.Auth.Errors;

/// <summary>
/// Turns unhandled failures into the same ProblemDetails envelope the endpoints
/// use. Detailing is not leaked to the caller; a malformed body is a validation
/// failure, everything else is INTERNAL.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception is BadHttpRequestException
            ? ApiProblem.Validation(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body could not be read."],
            })
            : ApiProblem.Internal();

        if (problem.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception while processing {Path}.", httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "Rejected request to {Path}.", httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = problem.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            problem.ToProblemDetails(),
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);

        return true;
    }
}
