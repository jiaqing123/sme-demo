using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SmeDemo.Api.Errors;

namespace SmeDemo.Api.Auth;

/// <summary>
/// Renders an org-policy denial with the §7.2 error envelope. A failed
/// authentication still falls through to the default handler, so JwtBearer's
/// OnChallenge produces the 401 envelope.
/// </summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // The fallback policy also runs when routing matched nothing. Keep the
        // pre-refactor 404 for unknown routes instead of challenging them.
        if (context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        if (authorizeResult.Forbidden)
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

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
