using SmeDemo.Api.Contracts;
using SmeDemo.Api.Readiness;

namespace SmeDemo.Api.Endpoints;

internal static class HealthEndpoints
{
    private const string Ready = "ready";
    private const string Degraded = "degraded";
    private const int RetryAfterSeconds = 5;

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness only: once the process can answer, it stays 200 even when a
        // dependency is down. Restarting the process would not fix that.
        app.MapGet("/healthz", () => TypedResults.Ok(new HealthResponse("ok")))
            .WithName("Health")
            .WithTags("Health")
            .WithSummary("Report process liveness.")
            .AllowAnonymous()
            .Produces<HealthResponse>(StatusCodes.Status200OK);

        app.MapGet("/readyz", async (
            HttpContext context,
            IReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await readiness.CheckAsync(cancellationToken);
            var body = new ReadinessResponse(
                snapshot.IsReady ? Ready : Degraded,
                snapshot.Checks
                    .Select(check => new ReadinessCheckResponse(
                        check.Name,
                        check.Ready ? Ready : Degraded))
                    .ToArray());

            if (snapshot.IsReady)
            {
                return Results.Ok(body);
            }

            // Tell an orchestrator how soon to try again without making the
            // caller parse the body for that timing.
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            return Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .WithName("Readiness")
            .WithTags("Health")
            .WithSummary("Report dependency readiness.")
            .AllowAnonymous()
            .Produces<ReadinessResponse>(StatusCodes.Status200OK)
            .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }
}
