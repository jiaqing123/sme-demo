using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmeDemo.Api.OpenApi;

/// <summary>
/// Completes the error contract in the generated document. ASP.NET Core
/// describes <c>ProblemDetails</c> with the five RFC 9457 fields, but every
/// response this API writes also carries <c>traceId</c> and an <c>error</c>
/// object, and a 429/503 carries a <c>Retry-After</c> header. Without these
/// additions a generated client sees an incomplete error shape.
/// </summary>
internal sealed class ProblemDetailsContractTransformer :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
    public const string ProblemSchemaName = "ProblemDetails";
    public const string RetryAfterHeader = "Retry-After";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Components?.Schemas is not { } schemas
            || !schemas.TryGetValue(ProblemSchemaName, out var schema))
        {
            return Task.CompletedTask;
        }

        // The schema may be registered directly or as a reference. Extending
        // the underlying component keeps the $ref sites unchanged.
        var target = schema is OpenApiSchemaReference reference ? reference.Target : schema;
        if (target is not OpenApiSchema problem)
        {
            return Task.CompletedTask;
        }

        problem.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        problem.Properties["traceId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Correlation id; the same value is returned in the X-Request-Id header.",
        };
        problem.Properties["error"] = BuildErrorSchema();

        problem.Required ??= new HashSet<string>(StringComparer.Ordinal);
        problem.Required.Add("status");
        problem.Required.Add("error");

        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var (statusCode, response) in operation.Responses!)
        {
            // Both envelopes can ask the caller to retry: 429 from the rate
            // limiter and 503 from a dependency outage.
            if (statusCode is not ("429" or "503"))
            {
                continue;
            }

            // The interface exposes headers as get-only, so an initially null
            // map has to be created on the concrete response.
            if (response.Headers is not { } headers)
            {
                if (response is not OpenApiResponse concrete)
                {
                    continue;
                }

                headers = new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
                concrete.Headers = headers;
            }

            headers[RetryAfterHeader] = new OpenApiHeader
            {
                Description = "Seconds to wait before retrying.",
                Required = false,
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "1",
                },
            };
        }

        return Task.CompletedTask;
    }

    private static OpenApiSchema BuildErrorSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Stable machine-readable error contract.",
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Stable error code, for example UNAUTHENTICATED.",
            },
            ["message"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Safe, human-readable explanation.",
            },
            ["errors"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Field-keyed validation messages; present only on validation failures.",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        },
        Required = new HashSet<string>(StringComparer.Ordinal) { "code", "message" },
    };
}
