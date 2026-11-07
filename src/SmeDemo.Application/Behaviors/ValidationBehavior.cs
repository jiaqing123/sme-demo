using System.Text;
using FluentValidation;
using MediatR;
using SmeDemo.Application.Errors;

namespace SmeDemo.Application.Behaviors;

/// <summary>
/// Runs every registered FluentValidation validator before the handler. A
/// failure becomes the API's VALIDATION_ERROR envelope; validators never touch a
/// database (requirements §5.5).
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var validators = _validators.ToArray();
        if (validators.Length == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(
            validators.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        var failures = results
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToArray();

        if (failures.Length > 0)
        {
            var errors = failures
                .GroupBy(failure => ToFieldName(failure.PropertyName), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(failure => failure.ErrorMessage).ToArray(),
                    StringComparer.Ordinal);

            throw new RequestValidationException(errors);
        }

        return await next();
    }

    // FluentValidation reports the C# property name ("Limit", "CardId"). The JSON
    // contract is snake_case ("limit", "card_id"), matching the hand-written
    // validation errors in the auth service ("username", "code").
    private static string ToFieldName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return propertyName;
        }

        var builder = new StringBuilder(propertyName.Length + 4);
        for (var index = 0; index < propertyName.Length; index++)
        {
            var current = propertyName[index];
            if (char.IsUpper(current))
            {
                if (index > 0 &&
                    (char.IsLower(propertyName[index - 1]) || char.IsDigit(propertyName[index - 1])))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
