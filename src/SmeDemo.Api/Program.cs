using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Endpoints;
using SmeDemo.Api.Errors;
using SmeDemo.Api.Http;
using SmeDemo.Api.OpenApi;
using SmeDemo.Application;
using SmeDemo.Infrastructure;
using SmeDemo.Infrastructure.Issuer;
using SmeDemo.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

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

// Minimal APIs and routing reject malformed JSON or an unknown route with an
// empty body before the exception handler sees it. Give those responses the
// same error envelope as a handler failure, matching the auth service.
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    var problem = response.StatusCode switch
    {
        StatusCodes.Status400BadRequest => ApiProblem.Validation(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body could not be read."],
        }),
        StatusCodes.Status404NotFound => ApiProblem.NotFound("The requested resource was not found."),
        _ => null,
    };

    if (problem is null)
    {
        return;
    }

    await response.WriteAsJsonAsync(
        problem.ToProblemDetails(),
        options: null,
        contentType: "application/problem+json");
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapResourceEndpoints();
await app.RunAsync();
