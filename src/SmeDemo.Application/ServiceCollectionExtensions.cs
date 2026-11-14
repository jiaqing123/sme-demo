using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Decorators;
using SmeDemo.Application.Features.Cards;
using SmeDemo.Application.Features.Me;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application;

/// <summary>
/// Composition root for the application layer. Handlers are registered one by
/// one on purpose: a missing or duplicated registration is a compile-time
/// decision here, not something a reflection scan has to be trusted to find.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssemblyContaining<GetTransactionsQueryValidator>();

        services.AddScoped<IQueryHandler<GetMeQuery, MeResponse>, GetMeHandler>();
        services.AddScoped<IQueryHandler<GetCardsQuery, CardListResponse>, GetCardsHandler>();
        services.AddScoped<IQueryHandler<GetCardArtQuery, CardArtResult>, GetCardArtHandler>();

        // Queries with a validator are wrapped in the validation decorator. The
        // concrete handler is resolved by the factory and never exposed as the
        // interface, so every consumer goes through validation.
        services.AddValidatedQuery<GetTransactionsQuery, TransactionListResponse, GetTransactionsHandler>();

        return services;
    }

    private static IServiceCollection AddValidatedQuery<TQuery, TResult, THandler>(
        this IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        services.AddScoped<THandler>();
        services.AddScoped<IQueryHandler<TQuery, TResult>>(provider =>
            new ValidationQueryHandler<TQuery, TResult>(
                provider.GetRequiredService<THandler>(),
                provider.GetServices<IValidator<TQuery>>()));

        return services;
    }
}
