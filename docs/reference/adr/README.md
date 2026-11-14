# Architecture Decision Record Index

- **Status:** living index of accepted decisions
- **Last updated:** 2026-11-14

This directory records decisions, not designs. Each row is an accepted decision,
a one-line summary, and the document where the reasoning and the current rule are
recorded. The source documents stay authoritative; this index exists so a new
engineer can find them without knowing the section numbers in advance.

## Status values

| Status | Meaning |
|--------|---------|
| Accepted | The decision is in force. Do not reverse it in a pull request without a new decision record. |
| Superseded | Replaced by a later ADR; kept for history. |

## Index

| ID | Decision | Status | Recorded in |
|----|----------|--------|-------------|
| ADR-001 | Use ASP.NET Core minimal APIs with explicit endpoint contracts; do not move to MVC or a third-party endpoint framework. | Accepted | [`../requirements.md`](../../requirements.md) §5.4, §5.5; [`../../history/handoff-reference-baseline.md`](../../history/handoff-reference-baseline.md) §3, decision 1 |
| ADR-002 | Dispatch commands and queries through interfaces owned by `Application`; no MediatR, AutoMapper, generic repository, or unit-of-work package. | Accepted | [`../requirements.md`](../../requirements.md) §13.2; [`../licensing.md`](../../licensing.md) §3; [`../native-di-dispatch.md`](../../native-di-dispatch.md) |
| ADR-003 | Authentication is default-deny: the API fallback policy requires an authenticated user, and public endpoints are explicitly anonymous. | Accepted | [`../requirements.md`](../../requirements.md) §5.3, §11.1; [`../threat-model.md`](../../threat-model.md) §4 (S10); [`../../../src/SmeDemo.Api/Program.cs`](../../../src/SmeDemo.Api/Program.cs) |
| ADR-004 | Organisation membership is an authorisation policy requirement, not a path-prefix middleware. | Accepted | [`../requirements.md`](../../requirements.md) §5.3; [`../../../src/SmeDemo.Api/Auth/OrgRequirement.cs`](../../../src/SmeDemo.Api/Auth/OrgRequirement.cs); [`../../../src/SmeDemo.Api/Auth/OrgAuthorizationHandler.cs`](../../../src/SmeDemo.Api/Auth/OrgAuthorizationHandler.cs) |
| ADR-005 | Cache policy is endpoint metadata; one middleware writes the header, and endpoints do not write `Cache-Control` directly. | Accepted | [`../../../src/SmeDemo.Api/Http/CacheControlMetadata.cs`](../../../src/SmeDemo.Api/Http/CacheControlMetadata.cs); [`../../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs`](../../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs) |
| ADR-006 | Contracts are executable: routes, authorisation, response metadata, cache policy, and OpenAPI are asserted by tests, and a new endpoint must pass them. | Accepted | [`../../../tests/SmeDemo.Architecture.Tests/ApiEndpointContractTests.cs`](../../../tests/SmeDemo.Architecture.Tests/ApiEndpointContractTests.cs); [`../../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs`](../../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs); [`../../../tests/SmeDemo.Api.Tests/OpenApiContractTests.cs`](../../../tests/SmeDemo.Api.Tests/OpenApiContractTests.cs); [`definition-of-done.md`](../definition-of-done.md) |
| ADR-007 | Compose is the deployment unit; no Kubernetes, microservices, or message queue is introduced at this size. | Accepted | [`../requirements.md`](../../requirements.md) §3, §10.6 |
| ADR-008 | Security is a release gate: threat-model review, abuse-case tests, and no new PII or secrets are merge conditions. | Accepted | [`../requirements.md`](../../requirements.md) §11; [`../threat-model.md`](../../threat-model.md) §5, §8; [`definition-of-done.md`](../definition-of-done.md) |
| ADR-009 | Cosmos DB is deferred, not rejected: the transaction feed stays on Table Storage behind `ITransactionStore`, and a second adapter can be added later. | Accepted | [`../requirements.md`](../../requirements.md) §8.6, §13.1 |
| ADR-010 | Schema changes are EF Core migrations applied as committed, idempotent SQL by a one-shot migrator; the application never applies schema at start-up and application logins cannot execute DDL. | Accepted | [`../requirements.md`](../../requirements.md) §8.8, §13.5; [`../development.md`](../../development.md#changing-a-schema) |
| ADR-011 | The mock issuer fixture is part of the demo contract; changing it changes the wallet and the acceptance screenshots. | Accepted | [`../requirements.md`](../../requirements.md) §13.4; [`../../../src/SmeDemo.Infrastructure/Issuer/IssuerSyncService.cs`](../../../src/SmeDemo.Infrastructure/Issuer/IssuerSyncService.cs) |
| ADR-012 | Keep the API version as the `ApiRoutes.Prefix` constant `/api/v1`; do not add an `Asp.Versioning` package now. A breaking change adds a new path segment with a documented deprecation window. | Accepted | [`versioning.md`](../versioning.md); [`../../../src/SmeDemo.Api/Endpoints/ApiRoutes.cs`](../../../src/SmeDemo.Api/Endpoints/ApiRoutes.cs) |
| ADR-013 | The OpenAPI document is a committed build artefact (`openapi/v1.json`), regenerated during `dotnet build` and checked by CI; the runtime endpoint is Development-only. | Accepted | [`../development.md`](../../development.md#openapi-document); [`../../../openapi/v1.json`](../../../openapi/v1.json); [`../../../src/SmeDemo.Api/SmeDemo.Api.csproj`](../../../src/SmeDemo.Api/SmeDemo.Api.csproj) |
| ADR-014 | This is an internal repository and is not distributed outside the company, so no open-source `LICENSE` file is added; external distribution would need a licence decision and `THIRD-PARTY-NOTICES`. | Accepted | [`../licensing.md`](../../licensing.md) §4; [`../../../README.md`](../../../README.md) |
| ADR-015 | Accept the roughly 3 GB footprint dominated by SQL Server; do not promise the stack runs anywhere without measuring on the actual demo hardware. | Accepted | [`../requirements.md`](../../requirements.md) §5.6, §13.3 |
| ADR-016 | Delivery is split: GitHub Actions stays the fast pull-request gate, and Azure Pipelines becomes the formal build and Test/UAT/Prod release path. Images are built once and promoted by digest. The deployment-placeholder part is superseded by ADR-020. | Accepted (deployment placeholder superseded by ADR-020) | [`../../azure-pipelines.md`](../../azure-pipelines.md); [`../../development.md`](../../development.md#continuous-integration); [`../../azure-pipelines.yml`](../../../azure-pipelines.yml); [`ADR-020`](ADR-020-azure-hosting-and-test-deployment.md) |
| ADR-017 | Rate limiting and readiness are per-instance runtime controls with in-memory limiters; forwarded-header trust is explicit and Compose-only. A deployment must not assume shared budgets, and production must scope trusted proxies instead of trusting one hop. | Accepted | [`ADR-017`](ADR-017-per-instance-rate-limiting-and-readiness.md); [`../../requirements.md`](../../requirements.md) §11.2–§11.3; [`../../threat-model.md`](../../threat-model.md) §4, §7; [`../../development.md`](../../development.md#auth-rate-limits) ([API rate limits](../../development.md#api-rate-limits), [forwarded headers](../../development.md#forwarded-headers)) |
| ADR-018 | Observability is opt-in OpenTelemetry with no default exporter and no collector; request ids are validated and logged; the SDK, base images, and scanner are pinned, every built image is scanned for fixable HIGH/CRITICAL issues with CycloneDX SBOMs, and the Compose runtime is read-only, capability-dropped, and resource-limited. | Accepted | [`ADR-018`](ADR-018-observability-and-supply-chain.md); [`../../observability.md`](../../observability.md); [`../../azure-pipelines.md`](../../azure-pipelines.md#container-image-hardening); [`../../development.md`](../../development.md#continuous-integration) |
| ADR-019 | The .NET runtime images are Microsoft chiseled Ubuntu images with no shell, package manager, or `curl`; Compose healthchecks run an in-image .NET probe, while `web` keeps its shell- and `wget`-based nginx image as the documented exception. | Accepted | [`ADR-019`](ADR-019-chiseled-runtime-images-and-health-probe.md); [`../../azure-pipelines.md`](../../azure-pipelines.md#container-image-hardening); [`../../development.md`](../../development.md#container-runtime-hardening) |
| ADR-020 | Hosting is Azure Container Apps with Azure SQL, Blob/Table Storage, Azure Files for signing keys, Key Vault, and a managed identity; the code stays on GitHub; Test deploys automatically after a green `main` build, UAT is manual, and Production requires approval; images are promoted by digest and scanned registry-side before deployment. | Accepted | [`ADR-020`](ADR-020-azure-hosting-and-test-deployment.md); [`../../azure-pipelines.md`](../../azure-pipelines.md); [`../../delivery.md`](../../delivery.md); [`../../runbook.md`](../../runbook.md) |

## Revisiting a decision

1. Read the recorded section and the code that implements it.
2. Write the proposed replacement in the pull request, including the security and
   delivery consequences.
3. Update the normative document and this index in the same change. Mark the old
   row `Superseded` and link the new one; do not delete history.
4. If the change affects a trust boundary, complete the threat-model review from
   [`../threat-model.md`](../../threat-model.md) §8 before merge.

Small implementation changes that stay inside an accepted decision do not need a
new record. A change to the decision itself does.
