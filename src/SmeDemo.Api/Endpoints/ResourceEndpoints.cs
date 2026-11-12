using SmeDemo.Api.Auth;
using SmeDemo.Api.RateLimiting;

namespace SmeDemo.Api.Endpoints;

internal static class ResourceEndpoints
{
    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();

        var api = app.MapGroup(ApiRoutes.Prefix)
            .WithTags("Resource API")
            .RequireAuthorization(AuthPolicies.ApiUser)
            .RequireRateLimiting(ApiRateLimitPolicies.ApiV1);

        api.MapMeEndpoints();
        api.MapCardEndpoints();

        return app;
    }
}
