using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const int MaxBodyLength = 64 * 1024;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// A deliberately small stand-in for an SMS gateway: it validates the message
// envelope, logs the message in plain text (that log is the demo's "delivery"),
// and returns a text/plain provider message id. Requirements §6.5.
app.MapPost("/messages", async (HttpRequest request) =>
{
    string? body;
    try
    {
        if (request.ContentLength > MaxBodyLength)
        {
            return ValidationError("body", "The request body must be 64 KB or smaller.");
        }

        var read = await ReadBodyAsync(request, MaxBodyLength);
        if (read.TooLarge)
        {
            return ValidationError("body", "The request body must be 64 KB or smaller.");
        }

        body = read.Body;
    }
    catch (IOException)
    {
        // A client disconnect or an oversized/chunked body surfaces here. The
        // contract keeps malformed input on the same 400 envelope.
        return ValidationError("body", "The request body could not be read.");
    }

    if (body is null)
    {
        return ValidationError("body", "The request body could not be read.");
    }

    SmsRequest? message;
    try
    {
        message = JsonSerializer.Deserialize<SmsRequest>(body);
    }
    catch (JsonException)
    {
        return ValidationError("body", "The request body could not be read as JSON.");
    }

    if (message is null)
    {
        return ValidationError("body", "The request body could not be read as JSON.");
    }

    var to = message.To?.Trim();
    var text = message.Text?.Trim();
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (string.IsNullOrEmpty(to))
    {
        errors["to"] = ["The 'to' field is required."];
    }
    else if (to.Length > 32)
    {
        errors["to"] = ["The 'to' field must be 32 characters or fewer."];
    }

    if (string.IsNullOrEmpty(text))
    {
        errors["text"] = ["The 'text' field is required."];
    }
    else if (text.Length > 1600)
    {
        errors["text"] = ["The 'text' field must be 1600 characters or fewer."];
    }

    if (errors.Count > 0)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "One or more validation errors occurred.",
                    errors,
                },
            },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var providerMessageId = $"sms-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";

    // The plaintext log is the point of the mock: the demo OTP must be visible in
    // `docker compose logs sms-mock`. The recipient and body are the payload, so
    // they are recorded; the caller IP is deliberately not.
    app.Logger.LogInformation("SMS to {To}: {Text}", to, text);

    return Results.Text(providerMessageId, "text/plain", statusCode: StatusCodes.Status202Accepted);
});

app.Run();

static async Task<(string? Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, int maxLength)
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var buffer = new char[4096];
    var text = new StringBuilder();

    int read;
    while ((read = await reader.ReadAsync(buffer)) > 0)
    {
        if (text.Length + read > maxLength)
        {
            return (null, true);
        }

        text.Append(buffer, 0, read);
    }

    var body = text.ToString();
    return (string.IsNullOrWhiteSpace(body) ? null : body, false);
}

static IResult ValidationError(string field, string message) =>
    Results.Json(
        new
        {
            error = new
            {
                code = "VALIDATION_ERROR",
                message = "One or more validation errors occurred.",
                errors = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [field] = [message],
                },
            },
        },
        statusCode: StatusCodes.Status400BadRequest);

internal sealed record SmsRequest(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("text")] string? Text);

public partial class Program;
