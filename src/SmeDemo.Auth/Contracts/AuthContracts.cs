using System.Text.Json.Serialization;

namespace SmeDemo.Auth.Contracts;

public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password);

public sealed record VerifyRequest(
    [property: JsonPropertyName("code")] string? Code);

public sealed record ChallengeResponse(
    [property: JsonPropertyName("challenge_id")] string ChallengeId,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("resend_in")] int ResendIn);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record DiscoveryResponse(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> SigningAlgorithms);

public sealed record JwksResponse(
    [property: JsonPropertyName("keys")] IReadOnlyList<IReadOnlyDictionary<string, object>> Keys);

public sealed record UserInfoResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("org")] string Org);

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status);
