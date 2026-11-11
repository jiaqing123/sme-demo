namespace SmeDemo.Application.Abstractions;

/// <summary>
/// A state-changing request that produces a <typeparamref name="TResult"/>.
/// The marker carries intent only; dispatch is the DI container's job.
/// </summary>
public interface ICommand<TResult>
{
}

/// <summary>
/// A read-only request that produces a <typeparamref name="TResult"/>.
/// </summary>
public interface IQuery<TResult>
{
}

/// <summary>
/// The result of a command that has nothing to return.
/// </summary>
public readonly record struct Unit;

/// <summary>
/// Handles exactly one command type. Implementations are registered explicitly
/// in <see cref="ServiceCollectionExtensions"/>; there is no assembly scan and
/// no runtime reflection.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles exactly one query type. Implementations are registered explicitly in
/// <see cref="ServiceCollectionExtensions"/>.
/// </summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// A command with no meaningful return value.
/// </summary>
public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit>
{
}
