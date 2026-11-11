using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmeDemo.Api.OpenApi;

/// <summary>
/// Declares the bearer token scheme in the generated document. The requirement
/// itself is applied per operation by <see cref="EndpointSecurityTransformer"/>,
/// so anonymous endpoints stay accurate.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "RS256 access token issued by the auth service.",
        };

        return Task.CompletedTask;
    }
}
