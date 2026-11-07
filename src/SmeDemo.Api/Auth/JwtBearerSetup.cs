using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SmeDemo.Api.Configuration;
using SmeDemo.Api.Errors;

namespace SmeDemo.Api.Auth;

/// <summary>
/// Wires the stock JwtBearer handler to the auth service's discovery document and
/// maps its 401s onto the §7.2 error codes. The handler fetches metadata over the
/// Compose network; the API never holds a signing key.
/// </summary>
public static class JwtBearerSetup
{
    private const string FailureItemKey = "sme.auth.failure";

    public static void Configure(JwtBearerOptions bearer, ApiOptions options)
    {
        bearer.MetadataAddress = options.AuthMetadataUrl;
        bearer.RequireHttpsMetadata = options.RequireHttpsMetadata;
        // Keep "sub"/"org"/"name" exactly as the issuer wrote them.
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = options.JwtAudience,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = "name",
        };

        bearer.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.Items[FailureItemKey] =
                    context.Exception is SecurityTokenExpiredException
                        ? ErrorCodes.TokenExpired
                        : ErrorCodes.InvalidToken;
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();

                var code = context.HttpContext.Items[FailureItemKey] as string
                    ?? ErrorCodes.Unauthenticated;
                var problem = code switch
                {
                    ErrorCodes.TokenExpired => ApiProblem.TokenExpired(),
                    ErrorCodes.InvalidToken => ApiProblem.InvalidToken(),
                    _ => ApiProblem.Unauthenticated("A bearer token is required."),
                };

                context.Response.StatusCode = problem.StatusCode;
                await context.Response.WriteAsJsonAsync(
                    problem.ToProblemDetails(),
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken: context.HttpContext.RequestAborted);
            },
        };
    }
}
