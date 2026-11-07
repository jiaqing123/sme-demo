using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmeDemo.IssuerMock;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("ISSUER_API_KEY")?.Trim() is { Length: > 0 } configured
    ? configured
    : "demo-issuer-key";
var fixture = IssuerFixture.Load();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet(
    "/issuer/v1/cards/{cardId:guid}/transactions",
    (Guid cardId, HttpRequest request) =>
    {
        if (!IsAuthorized(request, apiKey))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "UNAUTHORIZED",
                        message = "A valid X-Api-Key header is required.",
                    },
                },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return fixture.TryGetRaw(cardId, out var raw)
            ? Results.Text(raw, "application/json")
            : Results.Json(
                new
                {
                    error = new
                    {
                        code = "CARD_NOT_FOUND",
                        message = "The card was not found.",
                    },
                },
                statusCode: StatusCodes.Status404NotFound);
    });

app.Run();

static bool IsAuthorized(HttpRequest request, string apiKey)
{
    // Constant-time comparison: the key is a credential even in the demo.
    var provided = Encoding.UTF8.GetBytes(request.Headers["X-Api-Key"].ToString());
    var expected = Encoding.UTF8.GetBytes(apiKey);
    return provided.Length == expected.Length &&
           CryptographicOperations.FixedTimeEquals(provided, expected);
}

public partial class Program;
