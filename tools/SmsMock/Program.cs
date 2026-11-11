using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmeDemo.SmsMock;

const int MaxBodyLength = 64 * 1024;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// A deliberately small stand-in for an SMS gateway: it validates the message
// envelope, logs the message in plain text (that log is the demo's "delivery"),
// and returns a text/plain provider message id. Requirements §6.5.
app.MapPost("/messages", async (HttpRequest request) =>
{
    var (message, readError) = await ReadSmsAsync(request, MaxBodyLength);
    if (readError is not null) return readError;

    var to = message!.To?.Trim();
    var text = message.Text?.Trim();

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateField(errors, "to", to, maxLength: 32);
    ValidateField(errors, "text", text, maxLength: 1600);

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
    if (app.Logger.IsEnabled(LogLevel.Information))
    {
        app.Logger.LogInformation("SMS to {To}: {Text}", to, text);
    }

    return Results.Text(providerMessageId, "text/plain", statusCode: StatusCodes.Status202Accepted);
});

await app.RunAsync();

static async Task<(SmsRequest? Message, IResult? Error)> ReadSmsAsync(HttpRequest request, int maxBodyLength)
{
    const string BodyTooLarge = "The request body must be 64 KB or smaller.";
    const string BodyUnreadable = "The request body could not be read.";
    const string BodyNotJson = "The request body could not be read as JSON.";

    if (request.ContentLength > maxBodyLength)
        return (null, ValidationError("body", BodyTooLarge));

    string? body = null;
    try
    {
        (body, var tooLarge) = await ReadBodyAsync(request, maxBodyLength);
        if (tooLarge)
            return (null, ValidationError("body", BodyTooLarge));
    }
    catch (IOException)
    {
        // A client disconnect or an oversized/chunked body surfaces here. Fall
        // through to the null check so both paths share one error message.
    }

    if (body is null)
        return (null, ValidationError("body", BodyUnreadable));

    try
    {
        return JsonSerializer.Deserialize<SmsRequest>(body) is { } message
            ? (message, null)
            : (null, ValidationError("body", BodyNotJson));
    }
    catch (JsonException)
    {
        return (null, ValidationError("body", BodyNotJson));
    }
}

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

static void ValidateField(
    Dictionary<string, string[]> errors, string field, string? value, int maxLength)
{
    if (string.IsNullOrEmpty(value))
        errors[field] = [$"The '{field}' field is required."];
    else if (value.Length > maxLength)
        errors[field] = [$"The '{field}' field must be {maxLength} characters or fewer."];
}
