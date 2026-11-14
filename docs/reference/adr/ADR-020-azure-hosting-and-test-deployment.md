# ADR-020 — Azure hosting: Container Apps, Azure SQL, digest promotion, and a real Test deployment

- **Status:** Accepted
- **Date:** 2026-11-14
- **Owner:** Architecture
- **Deciders:** Architecture, Platform, Business owner (demo audience)
- **Supersedes:** the "deployment stays a failing placeholder" part of
  [ADR-016](README.md) (the split between GitHub Actions and Azure Pipelines is
  unchanged)

## Context

[ADR-016](README.md) split delivery in two: GitHub Actions stayed the fast
pull-request gate, and Azure Pipelines became the formal build and release path.
Its deployment stages were deliberately excluded by default and deliberately
failed once enabled, because five questions were unanswered:

1. which hosting model;
2. whether the code stays on GitHub;
3. how Test, UAT, and Production are gated;
4. the resource, registry, and variable naming;
5. whether GitHub Actions survives the transition.

Until they were answered, "release" could only mean a green `main` plus a
locally verified Compose stack. A release stage that did nothing and reported
success would have been worse than one that failed.

The answers are now recorded, together with the deployment implementation they
imply. The implementation is written, statically validated, and covered by the
same CI gates as the rest of the repository, but it has **not yet run against a
real Azure subscription** — see [§Verification status](#verification-status).

## Decision

### 1. Hosting model: Container Apps, with managed data services

| Option | Verdict |
|---|---|
| **Azure Container Apps** | **Chosen.** Managed, per-service scaling, built-in ingress with revisions and traffic splitting, a container job primitive for the migrator, and no cluster to operate. |
| App Service (Linux containers) | Rejected. Weak multi-container orchestration; the migrator and the two mocks each need an extra plan or mechanism. |
| AKS | Rejected. Most flexible, but it contradicts ADR-007 ("no Kubernetes at this size") and adds a cluster and its operators to a demo. |

Mapping:

| Compose service | Container Apps resource |
|---|---|
| `web` (nginx + SPA) | Container App, **external** ingress on 8080 |
| `auth` | Container App, **internal** ingress on 8081 |
| `api` | Container App, **internal** ingress on 8082 |
| `sms-mock` | Container App, internal ingress on 8083 |
| `issuer-mock` | Container App, internal ingress on 8084 |
| `db-migrator`, `db-migrator-auth`, `db-grants` | three Container Apps **Jobs** built from the same migrator image |
| `mssql` | **Azure SQL** Database, `sme_api` + `sme_auth` |
| `azurite` | **Azure Storage**: Blob container `card-art`, table `transactions` |
| `auth-keys` volume | **Azure Files** share, mounted read-write at `/keys` on `auth` |

The application services and both mocks run with `minReplicas = maxReplicas = 1`.
ADR-017's rate limits, challenge store, and readiness cache are per process; a
second replica would multiply the effective budgets and split the challenge
store. The platform probes replace Compose's in-image `HealthProbe`: `api`
publishes `/readyz` for readiness plus `/healthz` for liveness, `auth` and the
mocks publish `/healthz`, and `web` publishes `/`.

**Known limits of this landing:** no VNet integration and no private endpoints;
Azure SQL has the "Allow Azure services" firewall rule; the Storage account uses
shared-key authentication because the application still reads
`AZURE_STORAGE_CONNECTION_STRING`; there is no zone redundancy, no autoscaling,
no managed backup/restore exercise, and no WAF. These are recorded in
[`delivery.md`](../../delivery.md) as production gaps, not as
production-ready behaviour.

### 2. Code source: stay on GitHub

Azure Pipelines connects to the existing GitHub repository through a GitHub
service connection. Moving to Azure Repos has no benefit for this demo and would
lose the GitHub Actions PR feedback and the Dependabot loop. The pipeline YAML is
source-agnostic; only the trigger configuration changes if the code later moves.

### 3. Environment strategy: Test automatic, UAT manual, Production approved

| Stage | Trigger | Gate |
|---|---|---|
| **Test** | Every successful `Build` on `main` when `enableDeployments=true` | none beyond the build gates |
| **UAT** | Same run, after Test | Azure DevOps Environment `sme-demo-uat` approval/check — a human starts it |
| **Production** | After UAT | Environment `sme-demo-prod` approval, plus the business change ticket recorded in the run |

Every stage deploys the **same image digest** that Test verified. Promotion never
rebuilds. No release happens just because a PR merged: merging only produces a
release candidate, and the deployment run is a separate, deliberate act (see
[`delivery.md`](../../delivery.md) §4).

### 4. Identity, secrets, and network trust

- Every Container App and Job runs with one **user-assigned managed identity**,
  used for ACR pulls (`AcrPull`) and Key Vault secret reads
  (`Key Vault Secrets User`).
- Connection strings and the issuer API key live in **Key Vault** and are
  referenced from the Container App secrets by the managed identity. They are
  never committed, never baked into an image, and never passed as plain pipeline
  variables beyond the secure parameters of the deployment.
- `auth` keeps its signing keys at `/keys`, now on an Azure Files share so a new
  revision keeps the same key set. Losing that share invalidates every issued
  token; the share is the backup unit until a managed KMS/HSM replaces it.
- `web` is the only public entry point. `auth` and `api` have internal ingress,
  so `AUTH_TRUST_FORWARDED_HEADERS=true` stays safe: the Container Apps ingress is
  the only path to `auth`. A future TLS-terminating front end must re-evaluate
  that flag and must never expose `auth` directly.
- Internal service URLs are plain HTTP inside the environment, so
  `AUTH_REQUIRE_HTTPS_METADATA=false` is scoped to that environment. A TLS
  ingress in front of `auth` flips it back to `true`.

### 5. Image promotion and the registry-side scan

The Build stage builds each image once. With `publishImages=true` it pushes two
human-facing tags (commit SHA and build id), resolves the immutable digest for
each repository, publishes `images.json` as a pipeline artefact, and scans the
**promoted digests** with the same pinned Trivy gate as CI (`HIGH,CRITICAL`,
`--ignore-unfixed`, `--exit-code 1`). Test, UAT, and Production consume that
artefact; none of them rebuilds. The deployment record lists the six digests.

### 6. Naming

| Object | Pattern | Example (Test) |
|---|---|---|
| Resource group | `rg-sme-demo-<env>` (pre-created) | `rg-sme-demo-test` |
| Container Apps environment | `<namePrefix>-<env>-env` | `smedemo-test-env` |
| Container App | `<namePrefix>-<env>-<service>` | `smedemo-test-api` |
| Container Apps Job | `<namePrefix>-<env>-job-<purpose>` | `smedemo-test-job-migrate-api` |
| Azure Container Registry | `acrsmedemo<env>` (globally unique, pre-created) | `acrsmedemotest` |
| Key Vault | `kv-<env>-<hash>` | `kv-test-a1b2c3d4e5f6` |
| Azure SQL server | `<namePrefix>-<env>-sql-<hash>` | `smedemo-test-sql-a1b2c3d4e5f6` |
| Storage account | `st<hash>` | `sta1b2c3d4e5f6g7h8` |
| Service connections | `sc-sme-demo-azure`, `sc-sme-demo-acr` | — |
| Variable group | `vg-sme-demo-<env>` | `vg-sme-demo-test` |
| Environment | `sme-demo-<env>` | `sme-demo-test` |

`<namePrefix>` defaults to `smedemo` (capped at 10 characters because the
Container Apps 32-character limit applies to `<namePrefix>-<env>-job-migrate-auth`),
and the `<hash>` suffixes come from `uniqueString(...)` so the globally unique
names (Key Vault, SQL server, storage account) do not collide. The canonical,
copyable list is in [`azure-pipelines.md`](../../azure-pipelines.md#naming).

Region: `australiaeast` for the demo. Data residency, latency, and service
availability must be confirmed before a real customer deployment; `location` is a
pipeline variable, not a constant.

### 7. GitHub Actions stays

GitHub Actions remains the fast PR gate (the five checks in
[`delivery.md`](../../delivery.md) §2). Azure Pipelines owns the release path.
Neither is retired now; reducing GitHub Actions becomes a separate decision once
the Azure line has run green for several releases.

## Consequences

- The release path is now implementable and reviewable in Git: infrastructure is
  Bicep in `infra/`, orchestration is `tools/deploy/`, and the pipeline stages
  call them with environment-scoped variables instead of holding inline
  commands.
- The deployment is **declarative and idempotent**: re-running it deploys the
  same Bicep graph, re-applies the same idempotent SQL, and moves the apps to the
  given digests. The migrator gained an optional `BOOTSTRAP_SQL` step so Azure
  SQL gets the same two least-privilege application logins that Compose creates
  with `db-grants`.
- The web image renders its upstreams and DNS resolver from environment variables
  (`UPSTREAM_AUTH`, `UPSTREAM_API`, `NGINX_RESOLVER`). Compose keeps the old
  defaults, so the local stack is unchanged.
- Rollback moves traffic back to the previous Container App revision. It does
  **not** reverse a database migration: migrations stay forward-only and
  expand/contract (ADR-010), so a rollback is only safe while the previous
  application still tolerates the current schema.
- The first real deployment still requires one-time Azure preparations that
  cannot live in Git: a subscription and resource groups, an ACR, an ARM service
  connection, a Docker registry service connection, three Environments with
  approvals, and a variable group or Key Vault-backed secret source.
- Azure SQL, Storage, and Container Apps cost money while they exist. Tearing a
  demo environment down is a documented delete operation, not a scale-to-zero.

## Verification status

Implemented and statically validated in this repository, **not yet executed
against Azure** as of 2026-11-14. The following must be proven on the first real
Test deployment and recorded in the release notes before UAT is used:

1. Bicep deploys cleanly to an empty resource group, including the Key Vault
   secret references and the role assignments.
2. The three migrator jobs exit 0, the application logins can write their own
   database, and a second run of the jobs is a no-op.
3. `api`, `auth`, `web`, and both mocks become healthy, and the platform
   readiness probe on `/readyz` passes.
4. The Azure Files mount at `/keys` is writable by the non-root auth user; if it
   is not, apply the documented fallback (single revision, no mount, tokens reset
   per revision) and open a follow-up.
5. `environment-smoke.sh` passes against the public web origin, and the previous
   revision rollback path works.
6. The registry-side Trivy scan of the promoted digests passes.

If any of these fails, the fix lands with the same gates as this change; the ADR
is not "done" because the YAML exists.

## References

- [`../../azure-pipelines.md`](../../azure-pipelines.md) — pipeline wiring and
  one-time setup
- [`../../delivery.md`](../../delivery.md) — release cadence, checklist, gaps
- [`../../runbook.md`](../../runbook.md) — deploy, inspect, and roll back a
  Container Apps environment
- [`../../requirements.md`](../../requirements.md) §8.8 (schema ownership and
  least-privilege logins), §10 (operations), §11 (trust boundaries)
- [`../../threat-model.md`](../../threat-model.md) §4, §7 (accepted demo
  exceptions)
- ADR-007 (Compose is the local unit; no Kubernetes), ADR-010 (idempotent SQL,
  application logins without DDL), ADR-017 (per-instance limits, forwarded
  headers), ADR-018 (pinned, scanned images and SBOMs)
- Implementation: [`infra/main.bicep`](../../../infra/main.bicep),
  [`tools/deploy/`](../../../tools/deploy/), [`azure-pipelines.yml`](../../../azure-pipelines.yml),
  [`tools/Migrator`](../../../tools/Migrator), [`web/nginx`](../../../web/nginx)
