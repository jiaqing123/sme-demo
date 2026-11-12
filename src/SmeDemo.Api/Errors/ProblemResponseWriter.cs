using System.Globalization;

namespace SmeDemo.Api.Errors;

/// <summary>
/// The single place that turns an <see cref="ApiProblem"/> into an HTTP error
/// response. Every path that writes the §7.2 envelope goes through here so the
/// status code, content type, <c>instance</c>, <c>traceId</c> and optional
/// <c>Retry-After</c> header cannot drift between middleware, the exception
/// handler, the auth handlers and endpoint results.
/// </summary>
internal static class ProblemResponseWriter
{
    public const string ContentType = "application/problem+json";

    public static Task WriteAsync(
        HttpContext context,
        ApiProblem problem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        context.Response.StatusCode = problem.StatusCode;

        if (problem.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        return context.Response.WriteAsJsonAsync(
            problem.ToProblemDetails(context.Request.Path, context.TraceIdentifier),
            options: null,
            contentType: ContentType,
            cancellationToken: cancellationToken);
    }
}
