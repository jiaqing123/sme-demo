namespace SmeDemo.Api.RateLimiting;

/// <summary>
/// The single named limiter policy applied to the whole <c>/api/v1</c> group.
/// The partition key adds the endpoint name, so two endpoints on the same user
/// get independent budgets from one policy.
/// </summary>
public static class ApiRateLimitPolicies
{
    public const string ApiV1 = "api-v1-per-user-per-endpoint";
}
