using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmeDemo.Auth.Challenges;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Endpoints;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.Security;
using SmeDemo.Auth.Services;
using SmeDemo.Auth.Sms;

var builder = WebApplication.CreateBuilder(args);

// Development already validates the container at build time; apply the same
// fail-fast checks in every environment so a Production-only dependency error
// cannot survive until the first request.
builder.Host.UseDefaultServiceProvider(provider =>
{
    provider.ValidateOnBuild = true;
    provider.ValidateScopes = true;
});

var options = AuthOptions.FromConfiguration(builder.Configuration);

// Build the signing key at start-up so a bad key volume fails the container,
// not the first login.
var signingKeys = new SigningKeyStore(options.KeysPath);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(signingKeys);
builder.Services.AddSingleton<JwtTokenIssuer>();
builder.Services.AddSingleton<ChallengeStore>();
builder.Services.AddSingleton<IPasswordHasher<AuthUser>, PasswordHasher<AuthUser>>();
builder.Services.AddScoped<AuthFlowService>();
builder.Services.AddScoped<UserInfoService>();
builder.Services.AddDbContext<AuthDbContext>(db => db.UseSqlServer(options.DatabaseUrl));

if (options.SmsProviderUrl is null)
{
    builder.Services.AddSingleton<ISmsProvider, LoggingSmsProvider>();
}
else
{
    builder.Services.AddHttpClient<ISmsProvider, HttpSmsProvider>(
        client => client.Timeout = TimeSpan.FromSeconds(10));
}

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(options.CorsOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

// Minimal APIs and routing reject malformed JSON, an unknown route, or an
// unsupported verb with an empty body before the exception handler sees it.
// Give those responses the same envelope as a validator failure.
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

// Login challenges, tokens and the phone number in /auth/userinfo must not be
// cached by a proxy or the browser.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/auth"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
    }

    await next();
});

await AuthDbSeeder.SeedAsync(app.Services, options, CancellationToken.None);

app.MapAuthEndpoints();
await app.RunAsync();
