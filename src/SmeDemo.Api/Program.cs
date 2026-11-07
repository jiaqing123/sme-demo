using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Endpoints;
using SmeDemo.Api.Errors;
using SmeDemo.Application;
using SmeDemo.Infrastructure;
using SmeDemo.Infrastructure.Issuer;
using SmeDemo.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);
var options = ApiOptions.FromConfiguration(builder.Configuration);

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
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

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

// Account data must not sit in a shared or disk cache. Artwork keeps its own
// private cache policy, so only the /me routes are affected.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/me"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    await next();
});

app.UseAuthentication();
app.UseMiddleware<OrgClaimMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapResourceEndpoints();
app.Run();

public partial class Program;
