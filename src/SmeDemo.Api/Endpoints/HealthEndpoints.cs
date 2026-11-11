using SmeDemo.Api.Contracts;

namespace SmeDemo.Api.Endpoints;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("Health")
            .WithTags("Health")
            .WithSummary("Report process liveness.")
            .AllowAnonymous()
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        return app;
    }
}
