using Microsoft.AspNetCore.Diagnostics;
using SmeDemo.Application.Errors;

namespace SmeDemo.Api.Errors;

/// <summary>
/// Maps application failures to the §7.2 error envelope. Unhandled failures are
/// logged and returned as INTERNAL with no stack trace or message leak.
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
            RequestValidationException validation => ApiProblem.Validation(validation.Errors),
            ResourceForbiddenException forbidden => ApiProblem.Forbidden(forbidden.Message),
            ResourceNotFoundException notFound => ApiProblem.NotFound(notFound.Message),
            UnauthenticatedException unauthenticated => ApiProblem.Unauthenticated(unauthenticated.Message),
            BadHttpRequestException => ApiProblem.Validation(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body could not be read."],
            }),
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

        httpContext.Response.StatusCode = problem.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            problem.ToProblemDetails(),
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);

        return true;
    }
}
