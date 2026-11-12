using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Endpoints;
using SmeDemo.Api.Errors;
using SmeDemo.Api.Http;
using SmeDemo.Api.OpenApi;
using SmeDemo.Api.RateLimiting;
using SmeDemo.Api.Readiness;
using SmeDemo.Application;
using SmeDemo.Application.Abstractions;
using SmeDemo.Infrastructure;
using SmeDemo.Infrastructure.Issuer;
using SmeDemo.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

// Development already validates the container at build time; apply the same
// fail-fast checks in every environment so a Production-only dependency error
// cannot survive until the first request.
builder.Host.UseDefaultServiceProvider(provider =>
{
    provider.ValidateOnBuild = true;
    provider.ValidateScopes = true;
});

// The build-time OpenAPI generator (Microsoft.Extensions.ApiDescription.Server)
// invokes this entry point in the Production environment with no deployment
// configuration, then reads the document from the service container. Use inert
// options for that run so the document is produced from endpoint metadata
// alone; a deployed host never takes this path.
var isBuildTimeOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
var options = isBuildTimeOpenApi
    ? ApiOptions.ForOpenApiDocumentGeneration()
    : ApiOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddApplication(new ApplicationOptions(options.PublicBaseUrl));
builder.Services.AddInfrastructure(new InfrastructureOptions(
    options.DatabaseUrl,
    options.StorageConnectionString,
    options.TransactionsTable,
    options.CardArtContainer,
    options.OrgId,
    options.IssuerBaseUrl,
    options.IssuerApiKey,
    options.IssuerSyncInterval,
    options.IssuerSyncEnabled));

// Readiness: the database and blob probes are registered by AddInfrastructure
// because they own those adapters. The auth-metadata probe is composed here
// because it needs an HTTP client, and the aggregate service keeps endpoint
// code free of dependency health logic.
builder.Services.AddHttpClient<AuthMetadataReadinessProbe>(client =>
{
    // Well inside the aggregate 5s budget: a stalled metadata fetch must show
    // up as a degraded check, not as a hung probe.
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddScoped<IReadinessProbe>(provider =>
    provider.GetRequiredService<AuthMetadataReadinessProbe>());
builder.Services.AddScoped<IReadinessService, ReadinessService>();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.CorsOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(bearer => JwtBearerSetup.Configure(bearer, options));
builder.Services.AddAuthorization(authorization =>
{
    authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    authorization.AddPolicy(AuthPolicies.ApiUser, policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new OrgRequirement(options.OrgId)));
});
builder.Services.AddSingleton<IAuthorizationHandler, OrgAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

// One fixed-window policy for the /api/v1 group. The partition is the
// authenticated subject plus the endpoint name, so each endpoint has its own
// per-user budget and one hot endpoint cannot starve another.
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy(ApiRateLimitPolicies.ApiV1, context =>
    {
        var userId = context.User.FindFirst("sub")?.Value ?? "anonymous";
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? context.GetEndpoint()?.DisplayName
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"{userId}:{endpoint}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RateLimitPermit,
                Window = options.RateLimitWindow,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    });

    rateLimiter.OnRejected = async (context, cancellationToken) =>
    {
        if (context.HttpContext.Response.HasStarted)
        {
            return;
        }

        // Use the lease's own hint when available; the configured window keeps
        // Retry-After numeric and positive for any limiter that omits metadata.
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var fromLease)
            ? fromLease
            : options.RateLimitWindow;

        await ProblemResponseWriter.WriteAsync(
            context.HttpContext,
            ApiProblem.RateLimited("Too many requests. Try again later.", retryAfter),
            cancellationToken);
    };
});

builder.Services.AddOpenApi(openApi =>
{
    openApi.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    openApi.AddOperationTransformer<EndpointSecurityTransformer>();
});

if (options.SeedEnabled)
{
    builder.Services.AddHostedService<ApiDataSeeder>();
}

// Registered after the seeder so the first sync sees the seeded cards. The
// sync service is a background worker: its startup failures are logged and
// recorded, never fatal to the API host.
if (options.IssuerSyncEnabled)
{
    builder.Services.AddHostedService<IssuerSyncService>();
}

var app = builder.Build();

app.UseExceptionHandler();

// Minimal APIs and routing reject malformed JSON, an unknown route, or an
// unsupported verb with an empty body before the exception handler sees it.
// Give those responses the same error envelope as a handler failure, matching
// the auth service.
app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    var problem = context.Response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => ApiProblem.Validation(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body could not be read."],
        }),
        StatusCodes.Status404NotFound => ApiProblem.NotFound("The requested resource was not found."),
        StatusCodes.Status405MethodNotAllowed => ApiProblem.MethodNotAllowed(),
        _ => null,
    };

    if (problem is null)
    {
        return;
    }

    await ProblemResponseWriter.WriteAsync(context, problem);
});

app.UseCors();

// Cache policy is endpoint metadata, not a path check: the route declares the
// header value and this middleware writes it before the response starts. It
// runs before authentication so a 401/403 still carries the route's policy.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<CacheControlMetadata>() is { } cache)
    {
        context.Response.OnStarting(() =>
        {
            if (cache.ApplyToErrorResponses || context.Response.StatusCode < 400)
            {
                context.Response.Headers.CacheControl = cache.Value;
            }

            return Task.CompletedTask;
        });
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// After authentication/authorization so the partition key is a real subject and
// an anonymous call still gets its 401 instead of consuming a shared budget.
// Before endpoint execution so exhaustion happens without running the handler.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapResourceEndpoints();
await app.RunAsync();
