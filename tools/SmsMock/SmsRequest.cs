using System.Text.Json.Serialization;

namespace SmeDemo.SmsMock;

internal sealed record SmsRequest(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("text")] string? Text);
