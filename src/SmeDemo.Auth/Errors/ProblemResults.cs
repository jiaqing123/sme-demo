using Microsoft.AspNetCore.Mvc;

namespace SmeDemo.Auth.Errors;

public static class ProblemResults
{
    public static IResult From(ApiProblem problem) => new ProblemEnvelopeResult(problem);

    public static ProblemDetails ToProblemDetails(this ApiProblem problem, string instance, string traceId)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);

        var error = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = problem.Code,
            ["message"] = problem.Message,
        };

        if (problem.Errors is { Count: > 0 })
        {
            error["errors"] = problem.Errors;
        }

        var details = new ProblemDetails
        {
            Status = problem.StatusCode,
            Title = problem.Code,
            Detail = problem.Message,
            Type = $"https://httpstatuses.io/{problem.StatusCode}",
            Instance = instance,
        };
        details.Extensions["traceId"] = traceId;
        details.Extensions["error"] = error;
        return details;
    }

    /// <summary>
    /// Endpoint-result form of the envelope. It needs the live
    /// <see cref="HttpContext"/> to fill in <c>instance</c> and <c>traceId</c>,
    /// so a plain <see cref="Results.Json{TValue}(TValue, JsonSerializerOptions?, string?, int?)"/>
    /// call is not enough.
    /// </summary>
    private sealed class ProblemEnvelopeResult(ApiProblem problem) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            ProblemResponseWriter.WriteAsync(httpContext, problem, httpContext.RequestAborted);
    }
}
