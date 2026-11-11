namespace SmeDemo.Api.Http;

/// <summary>
/// Declares the <c>Cache-Control</c> value for an endpoint. A single middleware
/// applies the header on starting the response, so cache policy travels with
/// the route instead of being split between middleware path checks and handlers.
/// <paramref name="ApplyToErrorResponses"/> extends the policy to 4xx/5xx
/// responses as well; by default error responses keep whatever headers their
/// own producer set.
/// </summary>
public sealed record CacheControlMetadata(string Value, bool ApplyToErrorResponses = false);
