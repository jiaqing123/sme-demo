using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmeDemo.Auth.Challenges;
using SmeDemo.Auth.Configuration;
using SmeDemo.Auth.Endpoints;
using SmeDemo.Auth.Errors;
using SmeDemo.Auth.Persistence;
using SmeDemo.Auth.RateLimiting;
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
builder.Services.AddSingleton<LoginAccountRateLimiter>();
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

if (options.TrustForwardedHeaders)
{
    // Compose nginx is the only ingress in front of auth and appends the real
    // remote address to X-Forwarded-For, so trusting the right-most entry is
    // safe in that topology. The Compose network address is not loopback, which
    // is why the default known networks/proxies lists must be cleared. A
    // production deployment must instead list the exact ingress proxies/networks
    // it trusts; clearing them accepts a spoofed header from any direct caller.
    // KnownIPNetworks is the .NET 10 replacement for the obsolete KnownNetworks
    // property.
    builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
    {
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        forwarded.ForwardLimit = 1;
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
    });
}

// Per-IP fixed windows for the abuse-prone auth endpoints. The per-account
// login budget is enforced inside AuthFlowService because its key (the
// normalized username) only exists after request-shape validation.
builder.Services.AddRateLimiter(rateLimiter =>
{
    var limits = options.RateLimits;

    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy(
        AuthRateLimitPolicies.LoginIp,
        context => IpPartition(context, limits.LoginIpPermitLimit, limits.LoginIpWindow));
    rateLimiter.AddPolicy(
        AuthRateLimitPolicies.VerifyIp,
        context => IpPartition(context, limits.VerifyIpPermitLimit, limits.VerifyIpWindow));
    rateLimiter.AddPolicy(
        AuthRateLimitPolicies.ResendIp,
        context => IpPartition(context, limits.ResendIpPermitLimit, limits.ResendIpWindow));

    rateLimiter.OnRejected = async (context, cancellationToken) =>
    {
        if (context.HttpContext.Response.HasStarted)
        {
            return;
        }

        // Fixed-window leases carry their own retry hint. The one-second
        // fallback keeps Retry-After numeric even if a future limiter type does
        // not expose the metadata.
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var fromLease)
            ? fromLease
            : TimeSpan.FromSeconds(1);

        await ProblemResponseWriter.WriteAsync(
            context.HttpContext,
            ApiProblem.RateLimited("Too many requests. Try again later.", retryAfter),
            cancellationToken);
    };
});

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

if (options.TrustForwardedHeaders)
{
    // Must run before the rate limiter reads Connection.RemoteIpAddress.
    app.UseForwardedHeaders();
}

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

// After routing (so the endpoint's named policy is known) and before endpoint
// execution. The auth host has no authentication middleware, so no ordering
// constraint beyond that applies.
app.UseRateLimiter();

await AuthDbSeeder.SeedAsync(app.Services, options, CancellationToken.None);

app.MapAuthEndpoints();
await app.RunAsync();

static RateLimitPartition<string> IpPartition(HttpContext context, int permitLimit, TimeSpan window)
{
    // With forwarded headers trusted, UseForwardedHeaders has already replaced
    // RemoteIpAddress with the caller address from X-Forwarded-For. TestServer
    // leaves the address null, so a constant key keeps a host's tests
    // deterministic and still partitions them away from a real caller.
    var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
}
