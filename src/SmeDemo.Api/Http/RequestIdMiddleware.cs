using System.Diagnostics;

namespace SmeDemo.Api.Http;

/// <summary>
/// Gives every response an <c>X-Request-Id</c> and puts the same id on the
/// current activity and in a logging scope.
///
/// The middleware is registered first so authentication, rate limiting, the
/// status-code pages and the exception handler all observe it: a reset response
/// still gets the header because it is re-asserted in <c>OnStarting</c>.
/// </summary>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";

    private const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Resolve(context);

        context.TraceIdentifier = requestId;
        Activity.Current?.SetTag("request.id", requestId);

        context.Response.Headers[HeaderName] = requestId;
        context.Response.OnStarting(() =>
        {
            // The exception handler and status-code pages reset the response;
            // re-adding here keeps the correlation id on those envelopes.
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["request_id"] = requestId,
        }))
        {
            await _next(context);
        }
    }

    private static string Resolve(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName];
        if (inbound.Count == 1 && IsValid(inbound[0]))
        {
            return inbound[0]!;
        }

        // Prefer the trace id so logs, the response header and the exported
        // span all share one identifier. The Guid fallback covers callers
        // outside a traced activity (for example a build-time run).
        var traceId = Activity.Current?.TraceId;
        return traceId is { } id && id != default ? id.ToString() : Guid.NewGuid().ToString("N");
    }

    private static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= MaxLength }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');
}
