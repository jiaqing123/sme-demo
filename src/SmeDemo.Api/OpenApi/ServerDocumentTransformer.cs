using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using SmeDemo.Api.Configuration;

namespace SmeDemo.Api.OpenApi;

/// <summary>
/// Declares the deployed base URL in the generated document. Without an explicit
/// server, the committed document has none and a generated client has to guess
/// the host. The options are resolved from the application container so the
/// registration needs no constructor wiring.
/// </summary>
internal sealed class ServerDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var options = context.ApplicationServices.GetRequiredService<ApiOptions>();

        document.Servers =
        [
            new OpenApiServer
            {
                Url = options.PublicBaseUrl,
                Description = "Configured public base URL (`PUBLIC_BASE_URL`).",
            },
        ];

        return Task.CompletedTask;
    }
}
