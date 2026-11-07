using System.Reflection;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SmeDemo.Application.Behaviors;
using SmeDemo.Application.Features.Transactions;

namespace SmeDemo.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        ApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<GetTransactionsQuery>();
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddValidatorsFromAssemblyContaining<GetTransactionsQueryValidator>();

        // The per-map MaxDepth(64) in the profiles is the mitigation for the
        // advisory behind the licensing pin (see Directory.Build.props).
        services.AddAutoMapper(Assembly.GetExecutingAssembly());

        return services;
    }
}
