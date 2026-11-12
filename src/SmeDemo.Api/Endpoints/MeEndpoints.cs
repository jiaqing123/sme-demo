using Microsoft.AspNetCore.Http.HttpResults;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Http;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Features.Cards;
using SmeDemo.Application.Features.Me;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Api.Endpoints;

internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // Account data must not sit in a shared or disk cache.
        var me = app.MapGroup(ApiRoutes.Me)
            .WithTags("Me")
            .WithMetadata(new CacheControlMetadata("no-store", ApplyToErrorResponses: true))
            .RequireAuthorization(AuthPolicies.ApiUser);

        me.MapGet("", GetMe)
            .WithName("GetMe")
            .WithSummary("Return the caller's identity from the access token.")
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var cards = me.MapGroup(ApiRoutes.Cards).WithTags("Cards");

        cards.MapGet("", GetCards)
            .WithName("GetCards")
            .WithSummary("List the caller's cards in display order.")
            .Produces<CardListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        cards.MapGet("/{cardId:guid}/transactions", GetTransactions)
            .WithName("GetTransactions")
            .WithSummary("List a card's transactions with cursor pagination.")
            .Produces<TransactionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<Ok<MeResponse>> GetMe(
        CurrentUser user,
        IQueryHandler<GetMeQuery, MeResponse> handler,
        CancellationToken ct)
    {
        var response = await handler.HandleAsync(
            new GetMeQuery(user.Sub, user.Org, user.Name),
            ct);

        return TypedResults.Ok(response);
    }

    private static async Task<Ok<CardListResponse>> GetCards(
        CurrentUser user,
        IQueryHandler<GetCardsQuery, CardListResponse> handler,
        CancellationToken ct)
    {
        var response = await handler.HandleAsync(
            new GetCardsQuery(user.UserId),
            ct);

        return TypedResults.Ok(response);
    }

    private static async Task<Ok<TransactionListResponse>> GetTransactions(
        Guid cardId,
        [AsParameters] TransactionQuery query,
        CurrentUser user,
        IQueryHandler<GetTransactionsQuery, TransactionListResponse> handler,
        CancellationToken ct)
    {
        var response = await handler.HandleAsync(
            new GetTransactionsQuery(user.UserId, cardId, query.Cursor, query.Limit),
            ct);

        return TypedResults.Ok(response);
    }
}
