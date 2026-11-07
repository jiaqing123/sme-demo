using System.Text.Json.Serialization;

namespace SmeDemo.Api.Contracts;

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status);
