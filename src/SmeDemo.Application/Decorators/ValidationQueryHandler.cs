using System.Text;
using FluentValidation;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Errors;

namespace SmeDemo.Application.Decorators;

/// <summary>
/// Runs every registered FluentValidation validator before the decorated query
/// handler. A failure becomes the API's VALIDATION_ERROR envelope; validators
/// never touch a database (requirements §5.5).
///
/// This is the native replacement for MediatR's IPipelineBehavior: the
/// composition root wraps the handler that needs validation, and handlers
/// themselves never see a validator.
/// </summary>
public sealed class ValidationQueryHandler<TQuery, TResult> : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    private readonly IQueryHandler<TQuery, TResult> _inner;
    private readonly IReadOnlyList<IValidator<TQuery>> _validators;

    public ValidationQueryHandler(
        IQueryHandler<TQuery, TResult> inner,
        IEnumerable<IValidator<TQuery>> validators)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validators);

        _inner = inner;
        _validators = validators.ToArray();
    }

    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (_validators.Count == 0)
        {
            return await _inner.HandleAsync(query, cancellationToken);
        }

        var context = new ValidationContext<TQuery>(query);
        var results = await Task.WhenAll(
            _validators.Select(validator => validator.ValidateAsync(context, cancellationToken)));

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

        return await _inner.HandleAsync(query, cancellationToken);
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
