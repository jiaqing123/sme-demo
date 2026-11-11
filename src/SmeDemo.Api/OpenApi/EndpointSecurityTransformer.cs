using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmeDemo.Api.OpenApi;

/// <summary>
/// Marks each operation that needs a token with the bearer requirement. The
/// document does not carry a global requirement: an endpoint that is explicitly
/// anonymous (<c>/healthz</c>) must not be documented as authenticated, and an
/// empty <c>security</c> array does not survive serialization.
/// </summary>
internal sealed class EndpointSecurityTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (IsAnonymous(context))
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        if (operation.Security.Count == 0)
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                // An empty scope list is correct for a bearer token: OAuth scopes
                // do not apply, the token's claims carry the authorisation.
                [new OpenApiSecuritySchemeReference(BearerSecuritySchemeTransformer.SchemeName, context.Document)] = [],
            });
        }

        return Task.CompletedTask;
    }

    private static bool IsAnonymous(OpenApiOperationTransformerContext context)
    {
        var metadata = context.Description.ActionDescriptor?.EndpointMetadata;
        return metadata is not null && metadata.OfType<IAllowAnonymous>().Any();
    }
}
