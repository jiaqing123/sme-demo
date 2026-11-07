using Microsoft.AspNetCore.Mvc;

namespace SmeDemo.Api.Errors;

public static class ProblemResults
{
    public static IResult From(ApiProblem problem) =>
        Results.Json(
            problem.ToProblemDetails(),
            statusCode: problem.StatusCode,
            contentType: "application/problem+json");

    public static ProblemDetails ToProblemDetails(this ApiProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

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
        };
        details.Extensions["error"] = error;
        return details;
    }
}
