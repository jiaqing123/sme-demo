using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Contracts;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Services;

namespace SmeDemo.Auth.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The auth service has no authorization middleware: every route is
        // anonymous. The metadata is still explicit so the trust boundary is
        // visible on the endpoint and cannot be changed by a future fallback
        // policy without a deliberate edit here.
        app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")))
            .AllowAnonymous();

        app.MapGet("/.well-known/openid-configuration", (AuthOptions options) =>
            Results.Ok(new DiscoveryResponse(
                options.JwtIssuer,
                $"{options.JwtIssuer}/.well-known/jwks.json",
                ["RS256"])))
            .AllowAnonymous();

        app.MapGet("/.well-known/jwks.json", (SigningKeyStore keys) =>
            Results.Ok(new JwksResponse(keys.PublicJwks)))
            .AllowAnonymous();

        var auth = app.MapGroup("/auth");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous();
        auth.MapPost("/challenges/{challengeId}/verify", VerifyAsync)
            .AllowAnonymous();
        auth.MapPost("/challenges/{challengeId}/resend", ResendAsync)
            .AllowAnonymous();

        // Userinfo validates the bearer token inside UserInfoService rather than
        // through authorization middleware, so it is deliberately anonymous at
        // the endpoint level but still requires a valid token to return data.
        auth.MapGet("/userinfo", UserInfoAsync)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AuthFlowService flow,
        CancellationToken cancellationToken)
    {
        var result = await flow.LoginAsync(request, cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> VerifyAsync(
        string challengeId,
        VerifyRequest request,
        AuthFlowService flow,
        CancellationToken cancellationToken)
    {
        var result = await flow.VerifyAsync(challengeId, request, cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> ResendAsync(
        string challengeId,
        AuthFlowService flow,
        CancellationToken cancellationToken)
    {
        var result = await flow.ResendAsync(challengeId, cancellationToken);
        return ToHttpResult(result);
    }

    private static async Task<IResult> UserInfoAsync(
        HttpRequest request,
        UserInfoService userInfo,
        CancellationToken cancellationToken)
    {
        var result = await userInfo.GetAsync(request.Headers.Authorization.ToString(), cancellationToken);
        return ToHttpResult(result);
    }

    private static IResult ToHttpResult<T>(OperationResult<T> result) =>
        result.Succeeded
            ? Results.Ok(result.Value)
            : ProblemResults.From(result.Problem!);
}
