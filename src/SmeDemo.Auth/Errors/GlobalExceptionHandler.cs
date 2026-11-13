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
        var problem = exception switch
        {
            BadHttpRequestException badRequest => FromBadRequest(badRequest),
            _ => ApiProblem.Internal(),
        };

        if (problem.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception while processing {Path}.", httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning(exception, "Rejected request to {Path}.", httpContext.Request.Path);
        }

        await ProblemResponseWriter.WriteAsync(httpContext, problem, cancellationToken);

        return true;
    }

    /// <summary>
    /// The framework reports several request-shape failures as
    /// <see cref="BadHttpRequestException"/> and records the intended status on
    /// the exception. Preserve 413/415 instead of flattening every one to 400.
    /// </summary>
    private static ApiProblem FromBadRequest(BadHttpRequestException exception) =>
        exception.StatusCode switch
        {
            StatusCodes.Status413PayloadTooLarge => ApiProblem.PayloadTooLarge(),
            StatusCodes.Status415UnsupportedMediaType => ApiProblem.UnsupportedMediaType(),
            _ => ApiProblem.Validation(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body could not be read."],
            }),
        };
}
