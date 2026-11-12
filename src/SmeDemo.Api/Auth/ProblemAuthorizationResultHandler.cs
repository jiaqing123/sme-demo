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
        // The fallback policy also runs when routing matched nothing, and when
        // routing matched a path with the wrong verb. Neither case is an
        // authentication failure: let the request continue so the status-code
        // pages middleware renders the 404/405 envelope instead of a 401.
        if (context.GetEndpoint() is null || IsMethodNotAllowedEndpoint(context.GetEndpoint()))
        {
            await next(context);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var problem = ApiProblem.Forbidden("The token does not belong to this organisation.");
            await ProblemResponseWriter.WriteAsync(context, problem, context.RequestAborted);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>
    /// The matcher marks a verb mismatch with a bare framework endpoint whose
    /// display name is fixed by <c>HttpMethodMatcherPolicy</c>. It is the only
    /// endpoint that reaches authorization this way, and skipping its challenge
    /// does not weaken default-deny: every real endpoint still carries its own
    /// authorization metadata and never matches this marker.
    /// </summary>
    private static bool IsMethodNotAllowedEndpoint(Endpoint? endpoint) =>
        endpoint is not null &&
        string.Equals(endpoint.DisplayName, "405 HTTP Method Not Supported", StringComparison.Ordinal);
}
