using System.Security.Claims;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Contracts;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Features.Cards;
using SmeDemo.Application.Features.Me;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Api.Endpoints;

public static class ResourceEndpoints
{
    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")))
            .AllowAnonymous();

        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapGet("/me", GetMeAsync);
        api.MapGet("/me/cards", GetCardsAsync);
        api.MapGet("/me/cards/{cardId:guid}/transactions", GetTransactionsAsync);
        api.MapGet("/cards/{cardId:guid}/art", GetCardArtAsync);

        return app;
    }

    // Endpoints inject the handler they call. The call graph is visible in the
    // constructor-free signature and F12 lands on the handler.
    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetMeQuery, MeResponse> handler,
        CancellationToken cancellationToken)
    {
        var response = await handler.HandleAsync(
            new GetMeQuery(UserClaims.GetId(user), UserClaims.GetOrg(user), UserClaims.GetName(user)),
            cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetCardsAsync(
        ClaimsPrincipal user,
        IQueryHandler<GetCardsQuery, CardListResponse> handler,
        CancellationToken cancellationToken)
    {
        var response = await handler.HandleAsync(
            new GetCardsQuery(UserClaims.GetUserId(user)),
            cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetTransactionsAsync(
        Guid cardId,
        int? limit,
        string? cursor,
        ClaimsPrincipal user,
        IQueryHandler<GetTransactionsQuery, TransactionListResponse> handler,
        CancellationToken cancellationToken)
    {
        var response = await handler.HandleAsync(
            new GetTransactionsQuery(UserClaims.GetUserId(user), cardId, cursor, limit),
            cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetCardArtAsync(
        Guid cardId,
        ClaimsPrincipal user,
        IQueryHandler<GetCardArtQuery, CardArtResult> handler,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var artwork = await handler.HandleAsync(
            new GetCardArtQuery(UserClaims.GetUserId(user), cardId),
            cancellationToken);

        // Artwork is behind a bearer token; keep shared caches from serving it.
        response.Headers.CacheControl = "private, max-age=3600";
        return Results.Stream(artwork.Content, artwork.ContentType);
    }
}
