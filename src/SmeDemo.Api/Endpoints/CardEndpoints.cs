using Microsoft.AspNetCore.Http.HttpResults;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Http;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Features.Cards;

namespace SmeDemo.Api.Endpoints;

internal static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup(ApiRoutes.Cards).WithTags("Cards");

        // Artwork is behind a bearer token; keep shared caches from serving it.
        cards.MapGet("/{cardId:guid}/art", GetArt)
            .WithName("GetCardArt")
            .WithSummary("Stream a card's artwork.")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithMetadata(new CacheControlMetadata("private, max-age=3600"));

        return app;
    }

    private static async Task<FileStreamHttpResult> GetArt(
        Guid cardId,
        CurrentUser user,
        IQueryHandler<GetCardArtQuery, CardArtResult> handler,
        HttpResponse response,
        CancellationToken ct)
    {
        var art = await handler.HandleAsync(
            new GetCardArtQuery(user.UserId, cardId),
            ct);

        // An SVG opened directly is a document that can carry script. The
        // sandbox directive denies it active content while leaving the response
        // streamable as artwork; only the success path needs the header.
        response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";

        return TypedResults.Stream(art.Content, art.ContentType);
    }
}
