# Reference Baseline

- **Status:** reference baseline for this repository
- **Owner:** Architecture
- **Last updated:** 2026-11-11
- **Applies to:** `SmeDemo.Api`, `SmeDemo.Application`, `SmeDemo.Domain`,
  `SmeDemo.Infrastructure`, `SmeDemo.Auth`, and the delivery configuration in
  this repository.

## 1. What this is

This repository is a demo, but it is written to be a **reference baseline**: the
starting point for production systems built by this company. The UI, the data,
and the fixtures are disposable; the boundaries, contracts, and security defaults
are meant to be copied.

"Reference baseline" here means the smallest end-to-end system that still
demonstrates the decisions we do not want to rediscover per project:

- a real authentication boundary between a token issuer and a resource API;
- default-deny authorisation with object-level checks;
- explicit endpoint metadata rather than conventions discovered at runtime;
- executable contract and architecture tests that fail a pull request;
- a threat model with abuse cases, and security treated as a release gate;
- schema changes as reviewed, generated, idempotent SQL;
- a delivery pipeline that proves the images and Compose stack actually run.

It is not a production deployment. Plain HTTP, a fixed demo OTP, the Azurite
well-known key, and the other localhost-only shortcuts are registered in
[`../threat-model.md`](../threat-model.md) §7 and [`../requirements.md`](../requirements.md)
§11.3. Every one of them must be replaced before an internet-facing deployment.

## 2. Non-negotiables

A change that violates one of these does not belong in the baseline, even if it
is convenient for the demo.

1. **Default-deny authentication.** The API's fallback policy requires an
   authenticated user; a public endpoint is explicitly `AllowAnonymous()`. Do not
   add a route that is anonymous by accident.
2. **Endpoint metadata is part of the contract.** Every `/api/v1` endpoint has an
   authorisation policy, an endpoint name, a summary, and declared response
   types (including ProblemDetails for failures). The endpoint group and cache
   policy live in the endpoint file, not in a path-sniffing middleware.
3. **Contracts are executable.** A new endpoint, route, response shape, or
   OpenAPI change comes with contract tests. The architecture tests enforce the
   layering, endpoint-purity, auth-metadata, and endpoint-metadata rules; a
   violation must turn the suite red
   ([`tests/SmeDemo.Architecture.Tests`](../../tests/SmeDemo.Architecture.Tests)).
4. **Threat-model review is a release gate.** A new endpoint, data store, third
   party, authentication change, or new class of PII/financial data requires a
   review recorded against [`../threat-model.md`](../threat-model.md) §8 and the
   corresponding abuse-case tests before merge.

The full PR checklist is [`definition-of-done.md`](definition-of-done.md).

## 3. How to use these documents

- **New to the repository:** read this file, then
  [`../requirements.md`](../requirements.md) §5 (architecture), §7 (resource
  API), §8.8 (schema evolution), §11 (security), and §12 (acceptance). Read
  [`../threat-model.md`](../threat-model.md) before changing anything that
  crosses a trust boundary.
- **Adding a feature:** follow [`cookbook-add-resource.md`](cookbook-add-resource.md)
  for a worked, read-only resource. The same shape applies to commands.
- **Opening a pull request:** run every box in
  [`definition-of-done.md`](definition-of-done.md).
- **Changing the public API:** apply [`versioning.md`](versioning.md).
- **Making or revisiting a decision:** check the index in
  [`adr/README.md`](adr/README.md). Do not reopen a decision that is marked
  Accepted without a new decision record.
- **Running the stack and CI:** [`../development.md`](../development.md) has the
  commands, the schema workflow, the OpenAPI workflow, and the CI job list.

## 4. File map

| File | Purpose | Read it when |
|------|---------|--------------|
| [`README.md`](README.md) | What the reference baseline is and its non-negotiables. | Starting here. |
| [`definition-of-done.md`](definition-of-done.md) | Checkable PR and release checklist, including the security DoD. | Before every pull request. |
| [`adr/README.md`](adr/README.md) | Index of accepted decisions and where each is recorded. | You want to change an architectural rule. |
| [`cookbook-add-resource.md`](cookbook-add-resource.md) | End-to-end walkthrough for adding one read-only resource. | Adding a query or endpoint. |
| [`versioning.md`](versioning.md) | The ratified API versioning rule and what counts as breaking. | Changing a route, contract, or behaviour. |

Normative sources outside this directory:

| File | Relationship |
|------|--------------|
| [`../requirements.md`](../requirements.md) | System requirements and architecture. A reference document must not contradict it. |
| [`../threat-model.md`](../threat-model.md) | Security release gate: assets, trust boundaries, abuse cases, demo exceptions. |
| [`../development.md`](../development.md) | How to build, migrate, run, and reproduce CI locally. |
| [`../native-di-dispatch.md`](../native-di-dispatch.md) | Team standard for command/query dispatch. |
| [`../licensing.md`](../licensing.md) | Third-party licensing and the internal-repository distribution position. |
| [`../../tests/SmeDemo.Architecture.Tests`](../../tests/SmeDemo.Architecture.Tests) | Executable architecture rules: layering, endpoint purity, endpoint metadata, auth metadata. |
| [`../../openapi/v1.json`](../../openapi/v1.json) | Committed OpenAPI document generated by the API build. |
| [`../../README.md`](../../README.md) | Repository overview and quick start. |

## 5. Status

The baseline changes with the code. When a document here and the code disagree,
the code and the normative documents above win; fix the reference document in the
same pull request. The CI pipeline that proves the baseline is described in
[`../development.md`](../development.md); the image-build and Compose smoke-test
jobs run only in CI today. The Compose smoke has been exercised locally (6/6 on
2026-11-11), but the image job's clean-checkout proof and GitHub Actions itself
have not run yet; that caveat is recorded in the development guide.
