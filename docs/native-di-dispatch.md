# Native Command/Query Dispatch

- **Status:** team standard. Applies to all new application code.
- **Scope:** `src/SmeDemo.Application` and the endpoints that call it.
- **Decision record:** [`licensing.md`](licensing.md) §3 and
  [`requirements.md`](requirements.md) §13.2.

**Core rule: CQRS is a pattern, not a package.** Commands and queries are
dispatched through interfaces this repository owns. No mediator library, no
object-mapping library, and no runtime reflection are involved.

## 1. Contracts

Declared once in `Application/Abstractions/Messaging.cs`:

```csharp
public interface ICommand<TResult> { }
public interface IQuery<TResult> { }

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit> { }
```

Rules:

- A request is a `sealed record` that implements `ICommand<T>` or `IQuery<T>`.
- Handler signatures use only these interfaces plus BCL types. No external
  request or handler type may appear in the contract.
- One handler class per request. Do not share a handler between two requests.

## 2. Handlers

A handler is a plain class with constructor injection of the ports it uses:

```csharp
public sealed class GetCardsHandler : IQueryHandler<GetCardsQuery, CardListResponse>
{
    private readonly ICardRepository _cards;

    public GetCardsHandler(ICardRepository cards) => _cards = cards;

    public async Task<CardListResponse> HandleAsync(
        GetCardsQuery query,
        CancellationToken cancellationToken = default)
    {
        ...
    }
}
```

Rules:

- Inject **business dependencies only** (repositories, domain services, options).
  Logging, validation, and caching do not belong in the handler constructor;
  they are decorators (§4).
- Map to the response DTO at the call site. The projections in this repo are
  flat, so an explicit constructor call is cheaper to read than a mapper
  configuration.
- Handlers are `Scoped`.

## 3. Registration

Handlers are registered explicitly in `AddApplication`, one line per handler:

```csharp
services.AddScoped<IQueryHandler<GetMeQuery, MeResponse>, GetMeHandler>();
services.AddScoped<IQueryHandler<GetCardsQuery, CardListResponse>, GetCardsHandler>();
```

A query whose validator must run is registered through the decorator helper:

```csharp
services.AddValidatedQuery<GetTransactionsQuery, TransactionListResponse, GetTransactionsHandler>();
```

Rules:

- No assembly scanning at this size. A forgotten registration is a startup
  error, not a silently missing handler.
- If the number of handlers ever makes explicit registration a measured
  burden, a registration-only tool (Scrutor is MIT and established) may be
  introduced for scanning. It must not take part in dispatch, and decorators
  must remain explicit enough to trace in the composition root.
- Every handler is `Scoped`, matching the per-request DI scope and any
  `DbContext` it depends on.

## 4. Cross-cutting concerns: decorators

Validation, logging, and transaction handling wrap a handler; they are never
injected into it. The validation decorator lives in
`Application/Decorators/ValidationQueryHandler.cs` and is registered as a
factory that resolves the concrete handler and its validators:

```csharp
services.AddScoped<IQueryHandler<TQuery, TResult>>(provider =>
    new ValidationQueryHandler<TQuery, TResult>(
        provider.GetRequiredService<THandler>(),
        provider.GetServices<IValidator<TQuery>>()));
```

Rules:

- The decorator implements the same handler interface as the thing it wraps.
- Registration order is explicit; the last decorator registered is the
  outermost one.
- A failing validator throws `RequestValidationException`, which the API renders
  as `VALIDATION_ERROR` (requirements §7.2).

## 5. Dispatch

Endpoints inject the handler they call; there is no global dispatcher:

```csharp
private static async Task<IResult> GetCardsAsync(
    ClaimsPrincipal user,
    IQueryHandler<GetCardsQuery, CardListResponse> handler,
    CancellationToken cancellationToken)
{
    var response = await handler.HandleAsync(
        new GetCardsQuery(UserClaims.GetUserId(user)), cancellationToken);

    return Results.Ok(response);
}
```

Rules:

- `F12` on the injected interface leads to the handler; the call graph is fixed
  at compile time and has zero reflection cost.
- A dispatcher that takes `object` or `Type` is not allowed. If a future screen
  genuinely needs dynamic dispatch (for example a generic admin endpoint), that
  is a separate design discussion; do not reintroduce one by habit.

## 6. Lifetimes

| Service | Lifetime | Reason |
|---------|----------|--------|
| Command/query handlers | `Scoped` | Per-request scope; may depend on scoped ports such as `DbContext`. |
| Decorators | Follows the decorated handler | Registered per handler with the same scope. |
| Validators | `Scoped` or `Transient` | Stateless today; scoped if a validator gains request-scoped state. |

**Hard rule: no captive dependencies.** A singleton must not hold a scoped or
transient service. If a singleton needs a scoped dependency, create a scope with
`IServiceScopeFactory`. Keep `ValidateScopes` enabled in development so the
container rejects mismatches at startup.

## 7. Adding a feature

1. Add a `sealed record` request implementing `IQuery<T>` (or `ICommand<T>`).
2. Add the handler class implementing `IQueryHandler<,>` (or
   `ICommandHandler<,>`), with business dependencies only.
3. Register it in `AddApplication`; use `AddValidatedQuery` if it has a
   validator.
4. Inject the handler into the endpoint and call `HandleAsync`.
5. Map to the DTO at the call site.

## 8. Review checklist

- [ ] The request and handler use this repository's interfaces, not a
      third-party mediator's.
- [ ] Cross-cutting concerns are decorators; the handler constructor holds no
      logger, validator, or cache.
- [ ] The handler is `Scoped` unless the PR explains otherwise.
- [ ] No singleton captures a scoped dependency.
- [ ] `csproj` files contain no `MediatR` or `AutoMapper` reference.
- [ ] If a dispatcher exists, it is project-owned code, not a package.
