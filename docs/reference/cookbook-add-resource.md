# Cookbook: Add a Read-Only Resource

- **Status:** first draft, follows PR 1 of the reference baseline
- **Last updated:** 2026-11-11
- **Use when:** adding a query endpoint that reads existing data for the caller.

This walkthrough adds one read-only resource end to end. It uses a saved
beneficiary as the example: a user-owned record that is listed for the caller and
can be fetched by ID. The names are illustrative; the order and the files are the
point.

Read [`../requirements.md`](../requirements.md) §5.5 and
[`../threat-model.md`](../threat-model.md) §8 before starting. If the resource
adds a new data store, a third party, or a new class of PII, stop and do the
threat-model review first, not after the code compiles.

## 1. Decide the boundary and the data

Write down, before touching code:

- who owns the record (`UserId`, `OrgId`) and what the caller may see;
- whether the data already exists in `app.cards`, `auth.users`, Table Storage, or
  Blob Storage, or needs a new table;
- whether the response contains PII or financial data, and which threat-model
  assets and abuse cases apply;
- the route, the status codes, and the cache policy.

The baseline patterns are:

| Concern | Pattern in this repository |
|---------|----------------------------|
| Route prefix | `ApiRoutes.Prefix` (`/api/v1`) in [`ApiRoutes.cs`](../../src/SmeDemo.Api/Endpoints/ApiRoutes.cs) |
| Authorisation | `AuthPolicies.ApiUser` on the `/api/v1` group; per-record ownership in the handler |
| Cache | `CacheControlMetadata` on the endpoint group; account data uses `no-store` |
| Response | A record in `Application/Contracts`, returned through `TypedResults.Ok` |
| Errors | `ResourceNotFoundException` / `ResourceForbiddenException`, mapped by `GlobalExceptionHandler` |

## 2. Domain entity

Create `src/SmeDemo.Domain/Entities/Beneficiary.cs`. Keep invariants in the
entity and keep persistence concerns out of it, following
[`Card.cs`](../../src/SmeDemo.Domain/Entities/Card.cs):

```csharp
namespace SmeDemo.Domain.Entities;

public sealed class Beneficiary
{
    private Beneficiary()
    {
        // EF Core.
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string OrgId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string MaskedAccount { get; private set; } = string.Empty;

    public static Beneficiary Create(
        Guid id,
        Guid userId,
        string orgId,
        string name,
        string maskedAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(maskedAccount);

        return new Beneficiary
        {
            Id = id,
            UserId = userId,
            OrgId = orgId,
            Name = name,
            MaskedAccount = maskedAccount,
        };
    }
}
```

Do not add a full account number, PAN, or CVV to this type. `MaskedAccount` is
already the maximum the baseline stores for a payee.

## 3. Application contract, port, and query

The application layer owns the use case and the port; it does not reference EF
Core or the API project ([`../requirements.md`](../requirements.md) §5.5).

**Contract.** Add to `src/SmeDemo.Application/Contracts/` (a new file is fine):

```csharp
namespace SmeDemo.Application.Contracts;

public sealed record BeneficiaryDto(Guid Id, string Name, string MaskedAccount);

public sealed record BeneficiaryListResponse(IReadOnlyList<BeneficiaryDto> Items);
```

**Port.** Declare only the reads this use case needs, following
[`ICardRepository.cs`](../../src/SmeDemo.Application/Abstractions/ICardRepository.cs):

```csharp
using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Abstractions;

public interface IBeneficiaryRepository
{
    Task<IReadOnlyList<Beneficiary>> GetByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);

    Task<Beneficiary?> GetByIdAsync(
        Guid beneficiaryId,
        CancellationToken cancellationToken);
}
```

**Query and handler.** Create
`src/SmeDemo.Application/Features/Beneficiaries/GetBeneficiariesQuery.cs`,
following the shape of [`GetCardsQuery.cs`](../../src/SmeDemo.Application/Features/Cards/GetCardsQuery.cs):

```csharp
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Errors;
using SmeDemo.Domain.Entities;

namespace SmeDemo.Application.Features.Beneficiaries;

public sealed record GetBeneficiariesQuery(Guid UserId) : IQuery<BeneficiaryListResponse>;

public sealed class GetBeneficiariesHandler
    : IQueryHandler<GetBeneficiariesQuery, BeneficiaryListResponse>
{
    private readonly IBeneficiaryRepository _beneficiaries;

    public GetBeneficiariesHandler(IBeneficiaryRepository beneficiaries) =>
        _beneficiaries = beneficiaries;

    public async Task<BeneficiaryListResponse> HandleAsync(
        GetBeneficiariesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = await _beneficiaries.GetByUserAsync(query.UserId, cancellationToken);
        return new BeneficiaryListResponse(rows.Select(ToDto).ToArray());
    }

    private static BeneficiaryDto ToDto(Beneficiary beneficiary) =>
        new(beneficiary.Id, beneficiary.Name, beneficiary.MaskedAccount);
}
```

Add the by-ID query in the same feature folder. This is the handler that owns the
object-level check, not the endpoint:

```csharp
public sealed record GetBeneficiaryQuery(Guid UserId, Guid BeneficiaryId)
    : IQuery<BeneficiaryDto>;

public sealed class GetBeneficiaryHandler
    : IQueryHandler<GetBeneficiaryQuery, BeneficiaryDto>
{
    private readonly IBeneficiaryRepository _beneficiaries;

    public GetBeneficiaryHandler(IBeneficiaryRepository beneficiaries) =>
        _beneficiaries = beneficiaries;

    public async Task<BeneficiaryDto> HandleAsync(
        GetBeneficiaryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var row = await _beneficiaries.GetByIdAsync(query.BeneficiaryId, cancellationToken)
            ?? throw new ResourceNotFoundException("The beneficiary was not found.");

        if (row.UserId != query.UserId)
        {
            throw new ResourceForbiddenException("You do not have access to this beneficiary.");
        }

        return new BeneficiaryDto(row.Id, row.Name, row.MaskedAccount);
    }
}
```

The same rule as [`GetCardArtHandler`](../../src/SmeDemo.Application/Features/Cards/GetCardArtQuery.cs):
a missing record is `404`, another user's record is `403`, and the response must
not reveal whether the record exists.

If the query needs request-shape validation (a limit, a cursor, a filter format),
add a FluentValidation validator and register the query through the private
`AddValidatedQuery` helper in
[`ServiceCollectionExtensions.cs`](../../src/SmeDemo.Application/ServiceCollectionExtensions.cs).
Do not validate in the endpoint.

## 4. Infrastructure store

If the data already lives in an existing store and port, reuse that port and skip
to step 5.

For a new relational table:

1. Add `DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();` to
   [`AppDbContext.cs`](../../src/SmeDemo.Infrastructure/Persistence/AppDbContext.cs).
2. Add `Persistence/Configurations/BeneficiaryConfiguration.cs`, following
   [`CardConfiguration.cs`](../../src/SmeDemo.Infrastructure/Persistence/Configurations/CardConfiguration.cs):
   table `beneficiaries`, key on `Id` with `ValueGeneratedNever()`, the same
   `OrgId` / `UserId` / name lengths, and an index on `(UserId, Name)` if the list
   is ordered that way.
3. Add `Persistence/EfBeneficiaryRepository.cs`, following
   [`EfCardRepository.cs`](../../src/SmeDemo.Infrastructure/Persistence/EfCardRepository.cs).
   Use `AsNoTracking()`, filter by `UserId` in the query, and wrap store failures
   in `StoreUnavailableException` with the same catch predicate, so a database
   outage becomes `503 UPSTREAM_UNAVAILABLE`, not `500`.

Do not return `IQueryable` from the port and do not let the application layer see
`DbSet` or EF types.

## 5. DI registration

In [`ServiceCollectionExtensions.cs`](../../src/SmeDemo.Application/ServiceCollectionExtensions.cs),
add one line per handler inside `AddApplication`:

```csharp
services.AddScoped<
    IQueryHandler<GetBeneficiariesQuery, BeneficiaryListResponse>,
    GetBeneficiariesHandler>();

services.AddScoped<
    IQueryHandler<GetBeneficiaryQuery, BeneficiaryDto>,
    GetBeneficiaryHandler>();
```

In [`DependencyInjection.cs`](../../src/SmeDemo.Infrastructure/DependencyInjection.cs),
register the adapter next to the existing ports:

```csharp
services.AddScoped<IBeneficiaryRepository, EfBeneficiaryRepository>();
```

The explicit registration is intentional. There is no assembly scan; a missing
line is a visible composition-root change, not a runtime surprise.

## 6. Endpoint

Add the route constant to
[`ApiRoutes.cs`](../../src/SmeDemo.Api/Endpoints/ApiRoutes.cs):

```csharp
public const string Beneficiaries = "/beneficiaries";
```

Create `src/SmeDemo.Api/Endpoints/BeneficiaryEndpoints.cs`. Follow
[`MeEndpoints.cs`](../../src/SmeDemo.Api/Endpoints/MeEndpoints.cs) and
[`CardEndpoints.cs`](../../src/SmeDemo.Api/Endpoints/CardEndpoints.cs):

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using SmeDemo.Api.Auth;
using SmeDemo.Api.Http;
using SmeDemo.Application.Abstractions;
using SmeDemo.Application.Contracts;
using SmeDemo.Application.Features.Beneficiaries;

namespace SmeDemo.Api.Endpoints;

internal static class BeneficiaryEndpoints
{
    public static IEndpointRouteBuilder MapBeneficiaryEndpoints(this IEndpointRouteBuilder app)
    {
        // Account data must not sit in a shared cache. The policy is repeated
        // from the /api/v1 group so the endpoint file states its own contract.
        var beneficiaries = app.MapGroup(ApiRoutes.Beneficiaries)
            .WithTags("Beneficiaries")
            .WithMetadata(new CacheControlMetadata("no-store", ApplyToErrorResponses: true))
            .RequireAuthorization(AuthPolicies.ApiUser);

        beneficiaries.MapGet("", GetBeneficiaries)
            .WithName("GetBeneficiaries")
            .WithSummary("List the caller's saved beneficiaries.")
            .Produces<BeneficiaryListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        beneficiaries.MapGet("/{beneficiaryId:guid}", GetBeneficiary)
            .WithName("GetBeneficiary")
            .WithSummary("Return one of the caller's saved beneficiaries.")
            .Produces<BeneficiaryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<Ok<BeneficiaryListResponse>> GetBeneficiaries(
        CurrentUser user,
        IQueryHandler<GetBeneficiariesQuery, BeneficiaryListResponse> handler,
        CancellationToken ct)
    {
        var response = await handler.HandleAsync(new GetBeneficiariesQuery(user.UserId), ct);
        return TypedResults.Ok(response);
    }

    private static async Task<Ok<BeneficiaryDto>> GetBeneficiary(
        Guid beneficiaryId,
        CurrentUser user,
        IQueryHandler<GetBeneficiaryQuery, BeneficiaryDto> handler,
        CancellationToken ct)
    {
        var response = await handler.HandleAsync(
            new GetBeneficiaryQuery(user.UserId, beneficiaryId),
            ct);

        return TypedResults.Ok(response);
    }
}
```

Then register the new group in
[`ResourceEndpoints.cs`](../../src/SmeDemo.Api/Endpoints/ResourceEndpoints.cs):

```csharp
api.MapMeEndpoints();
api.MapCardEndpoints();
api.MapBeneficiaryEndpoints();
```

The group repeats `RequireAuthorization(AuthPolicies.ApiUser)` even though the
parent `/api/v1` group already applies it; the endpoint file is readable on its
own. If the endpoint is mounted outside that group, the explicit policy is what
keeps it protected. The architecture test will fail an unauthenticated
`/api/v1` endpoint.

Checklist for the endpoint file:

- `MapGroup` under `ApiRoutes.Prefix`, a tag, and the cache metadata;
- `WithName` unique across the application;
- `WithSummary` written for a client reader;
- `Produces<T>` for every success shape and `ProducesProblem` for every failure
  the endpoint can return;
- `CurrentUser` for the caller identity, never claims parsed by hand;
- no `DbContext`, `TableClient`, `BlobClient`, `HttpClient`, or response-header
  writing in the endpoint.

## 7. Tests

### Handler test

Add a test in `tests/SmeDemo.Application.Tests` (follow
[`GetCardsHandlerTests.cs`](../../tests/SmeDemo.Application.Tests/GetCardsHandlerTests.cs))
that asserts the list is filtered by `UserId` and mapped to the contract.

### Contract tests

The API contract is executable. Update both files:

- [`EndpointContractTests.cs`](../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs):
  add `/api/v1/beneficiaries` and `/api/v1/beneficiaries/{beneficiaryId:guid}` to
  `ExpectedApiRoutes`, and add the routes to the OpenAPI path assertion.
- [`OpenApiContractTests.cs`](../../tests/SmeDemo.Api.Tests/OpenApiContractTests.cs):
  add `/api/v1/beneficiaries` and `/api/v1/beneficiaries/{beneficiaryId}` to
  `ApiRoutes` so the Bearer requirement is asserted for them (OpenAPI drops the
  `:guid` route constraint from the path key).

The existing endpoint metadata test will fail until the new endpoints have
authorisation, a name, and at least one response type. That failure is the point;
do not weaken the test.

### Architecture tests

The suite in
[`tests/SmeDemo.Architecture.Tests`](../../tests/SmeDemo.Architecture.Tests)
checks every `/api/v1` endpoint from the real host, the project dependency
direction, endpoint purity, and the auth endpoint metadata. A correct new
endpoint should need no change here. If the suite fails, fix the endpoint or the
registration; do not add a special case for the new route.

### Abuse-case tests (required)

Add the security tests from
[`../threat-model.md`](../threat-model.md) §5 to
`tests/SmeDemo.Api.Tests/ResourceApiTests.cs`, using
[`ApiTestFactory`](../../tests/SmeDemo.Api.Tests/ApiTestFactory.cs) and the
`OtherUserId` fixture:

| Case | Expected | Pattern |
|------|----------|---------|
| Another user's beneficiary ID | `403 FORBIDDEN` | `CardArt_OtherUsersCard_ReturnsForbidden` |
| Unknown beneficiary ID | `404 NOT_FOUND` | `Transactions_UnknownCard_ReturnsNotFound` |
| Wrong `org` in the token | `403 FORBIDDEN` | `Cards_WithWrongOrg_ReturnsForbidden` |
| No token | `401 UNAUTHENTICATED` | `Cards_WithoutToken_ReturnsUnauthenticated` |
| List request | Only the caller's rows; no other user's ID appears | Handler test plus API test |

The other user's row must be seeded in `ApiTestFactory` with a different
`UserId`. Assert the error envelope code, not only the status.

## 8. Regenerate OpenAPI

The document is generated during `dotnet build`, not written by hand:

```powershell
dotnet build src/SmeDemo.Api/SmeDemo.Api.csproj --configuration Release
git diff -- openapi/v1.json
```

The new paths and operations appear in
[`openapi/v1.json`](../../openapi/v1.json). If the build did not change the file,
the endpoints are not being discovered: check that `MapBeneficiaryEndpoints` is
called from `MapResourceEndpoints` and that the project still references
`Microsoft.Extensions.ApiDescription.Server`.

Commit the regenerated file with the code. CI rebuilds and fails on a diff, and
`OpenApiContractTests.CommittedDocument_MatchesTheDocumentServedAtRuntime` fails
if the committed file and the runtime document disagree.

## 9. Migration and SQL

If step 4 added or changed a table, create the migration and regenerate the
committed script exactly as in
[`../development.md`](../development.md#changing-a-schema):

```powershell
dotnet tool run dotnet-ef migrations add AddBeneficiaries `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure `
  --output-dir Persistence/Migrations

dotnet tool run dotnet-ef migrations script --idempotent `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure `
  --output db/migrations/api/sme_api.sql
```

Read the generated `Up()` before continuing. The auth schema is untouched; there
is no cross-database migration. Commit the entity change, the migration, and the
script together, then run the pending-migration check in
[`definition-of-done.md`](definition-of-done.md).

If the resource reuses an existing table and store, there is no migration; say so
in the pull request rather than adding an empty one.

## 10. Threat model and documentation

Update the documents that make the change reviewable:

- [`../threat-model.md`](../threat-model.md) §2 (assets) if a new data class
  exists;
- §4 (STRIDE rows) and §5 (abuse cases) for the new object-level check and any
  new failure mode;
- §7 if the implementation introduces a demo shortcut (for example, a new
  localhost-only bypass). Do not invent a shortcut to avoid a control.
- [`../requirements.md`](../requirements.md) §7 (resource table and response
  shape), §8 (data model), and §11.3 if a shortcut is added.
- [`../development.md`](../development.md) only if the run or migration workflow
  changes.

Record the review in the pull request with the section that changed. A new
endpoint with no review is not done, even when the tests are green.

## 11. Final checklist

- [ ] Domain entity and invariant live in `SmeDemo.Domain`.
- [ ] Query/handler and contract live in `Application`; the port is the only
      persistence dependency.
- [ ] Adapter and EF configuration live in `Infrastructure`; DI is explicit.
- [ ] Endpoint is under `/api/v1`, uses `CurrentUser`, `ApiUser`, endpoint
      metadata, `Produces`/`ProducesProblem`, and cache metadata.
- [ ] Handler tests, contract tests, and the IDOR/unknown-ID abuse cases pass.
- [ ] `openapi/v1.json` is regenerated and committed.
- [ ] Migration and generated SQL are committed when the schema changed.
- [ ] Threat-model review and docs are updated.
- [ ] [`definition-of-done.md`](definition-of-done.md) is fully ticked.
