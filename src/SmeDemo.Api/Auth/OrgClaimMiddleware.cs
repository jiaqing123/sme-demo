using SmeDemo.Api.Configuration;
using SmeDemo.Api.Errors;

namespace SmeDemo.Api.Auth;

/// <summary>
/// One place for the cross-cutting org check from requirements §5.3. A token is
/// only a permission to see data once its <c>org</c> equals the configured
/// <c>ORG_ID</c>; handlers still scope every query by <c>sub</c>.
/// </summary>
public sealed class OrgClaimMiddleware
{
    private readonly RequestDelegate _next;

    public OrgClaimMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ApiOptions options)
    {
        // Liveness and the OpenAPI document stay anonymous; the org check only
        // guards the resource endpoints.
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var org = context.User.FindFirst("org")?.Value;
            if (!string.Equals(org, options.OrgId, StringComparison.Ordinal))
            {
                var problem = ApiProblem.Forbidden("The token does not belong to this organisation.");
                context.Response.StatusCode = problem.StatusCode;
                await context.Response.WriteAsJsonAsync(
                    problem.ToProblemDetails(),
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken: context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }
}
