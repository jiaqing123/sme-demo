using System.Net.Sockets;
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
            OnAuthenticationFailed = async context =>
            {
                // A metadata/JWKS fetch failure means the authority could not be
                // reached, not that the caller's token is bad. Keep that distinct
                // from the token-failure codes so OnChallenge can answer 503.
                context.HttpContext.Items[FailureItemKey] =
                    await IsMetadataTransportFailureAsync(context)
                        ? ErrorCodes.UpstreamUnavailable
                        : context.Exception is SecurityTokenExpiredException
                            ? ErrorCodes.TokenExpired
                            : ErrorCodes.InvalidToken;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();

                var code = context.HttpContext.Items[FailureItemKey] as string
                    ?? ErrorCodes.Unauthenticated;
                var problem = code switch
                {
                    ErrorCodes.UpstreamUnavailable => ApiProblem.UpstreamUnavailable(),
                    ErrorCodes.TokenExpired => ApiProblem.TokenExpired(),
                    ErrorCodes.InvalidToken => ApiProblem.InvalidToken(),
                    _ => ApiProblem.Unauthenticated("A bearer token is required."),
                };

                await ProblemResponseWriter.WriteAsync(
                    context.HttpContext,
                    problem,
                    context.HttpContext.RequestAborted);
            },
        };
    }

    /// <summary>
    /// Distinguishes "the issuer could not be reached" from "the token is
    /// invalid". Signature, issuer, audience, lifetime and algorithm failures are
    /// token-validation exceptions and never match the transport rule.
    /// </summary>
    private static async Task<bool> IsMetadataTransportFailureAsync(AuthenticationFailedContext context)
    {
        // A caller that hung up is not an upstream outage: leave its request
        // aborted instead of turning it into a 503 (and a misleading retry hint).
        if (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        // A configuration-manager failure that escapes to the event is wrapped in
        // InvalidOperationException (IDX20803), with the socket/HTTP fault inside.
        if (ContainsTransportFailure(context.Exception, depth: 0))
        {
            return true;
        }

        if (!IsSigningKeyNotFound(context.Exception) || context.Options.ConfigurationManager is not { } manager)
        {
            return false;
        }

        // .NET 10's JsonWebTokenHandler catches a failed metadata retrieval
        // (logging IDX10261), validates with an empty key set, and reports only
        // IDX10500 "no security keys were provided" to this event. Ask the
        // manager again: a cached configuration answers immediately, while an
        // unreachable authority reproduces the transport exception here.
        // Bound the re-check: a blackholed authority must not hold the request
        // for the metadata client's full time-out. The caller's abort still wins
        // through the linked token.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            context.HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await manager.GetConfigurationAsync(timeout.Token);
            return false;
        }
        catch (Exception) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up while we re-checked; never call that a 503.
            return false;
        }
        catch (Exception exception)
        {
            return ContainsTransportFailure(exception, depth: 0);
        }
    }

    private static bool IsSigningKeyNotFound(Exception? exception) => exception switch
    {
        SecurityTokenSignatureKeyNotFoundException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsSigningKeyNotFound),
        _ => false,
    };

    private static bool ContainsTransportFailure(Exception? exception, int depth)
    {
        // Bound the walk: configuration wrappers are shallow and this must not
        // loop on a self-referential exception chain.
        if (exception is null || depth > 4)
        {
            return false;
        }

        if (exception is HttpRequestException or IOException or SocketException
            or TimeoutException or OperationCanceledException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(inner => ContainsTransportFailure(inner, depth + 1));
        }

        // Only the known configuration-manager wrapper is peeled. Looking at
        // every inner exception would risk reclassifying a validation failure.
        return exception is InvalidOperationException
            && ContainsTransportFailure(exception.InnerException, depth + 1);
    }
}
