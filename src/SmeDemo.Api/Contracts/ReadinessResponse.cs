using System.Text.Json.Serialization;

namespace SmeDemo.Api.Contracts;

/// <summary>
/// The readiness document: one status for the aggregate plus one entry per
/// dependency. It intentionally carries no error messages, URLs, or exception
/// detail so a probe is safe to expose anonymously.
/// </summary>
public sealed record ReadinessResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checks")] IReadOnlyList<ReadinessCheckResponse> Checks);

public sealed record ReadinessCheckResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status);
