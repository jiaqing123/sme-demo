# Azure Pipelines Guide

This document describes the Azure Pipelines delivery pipeline in
[`azure-pipelines.yml`](../azure-pipelines.yml). It complements the GitHub
Actions workflow in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml):
GitHub Actions remains the fast pull-request feedback loop, and Azure Pipelines
is the formal build, environment promotion, and release pipeline. The hosting
decision is recorded in
[ADR-020](reference/adr/ADR-020-azure-hosting-and-test-deployment.md). The
cadence, promotion-by-digest rules, and release checklist are in
[`delivery.md`](delivery.md); this document describes the pipeline wiring and
the one-time Azure setup.

The Build stage mirrors the GitHub Actions gates. The Test, UAT, and Production
stages are implemented: they call [`tools/deploy/deploy.sh`](../tools/deploy/deploy.sh),
which deploys [`infra/main.bicep`](../infra/main.bicep), runs the three migrator
jobs in order, waits for the new revisions, records the deployment, and runs the
public smoke test. The stages are still excluded unless `enableDeployments=true`,
because a run needs the one-time Azure preparation in
[Required Azure DevOps configuration](#required-azure-devops-configuration).

The implementation has been statically validated by review, `az bicep build`,
`bash -n`, and the normal Build gates. It has **not yet executed against a real
Azure subscription**; ADR-020's
[Verification status](reference/adr/ADR-020-azure-hosting-and-test-deployment.md#verification-status)
lists what the first Test deployment must prove.

## Pipeline at a glance

```text
trigger / PR
    │
    ▼
Build
 ├─ DotNet            build, tests, format, OpenAPI, EF/SQL, package and secret gates (SDK pinned to global.json)
 ├─ Web               npm ci, build, test
 ├─ ComposeValidation docker compose config -q, az bicep build, bash -n on the deploy scripts
 ├─ Images            build six images; push + resolve digests + scan them, or build + SBOM + scan locally
 └─ ComposeSmoke      start the minimal stack, wait, run the smoke script, tear down
    │
    ▼
Deploy Test           Environment: sme-demo-test; automatic after a green Build
    │
    ▼
Deploy UAT            Environment: sme-demo-uat; Environment approval is the manual gate
    │
    ▼
Deploy Production     Environment: sme-demo-prod; approval plus change ticket
```

UAT and Production have no queue-time trigger of their own. Their manual gate is
the Azure DevOps Environment approval/check, so a release run sets
`enableDeployments=true` and the same run walks Test → UAT → Production, pausing
at each gated Environment until an approver acts.

The deployment stages are compile-time conditional on the
`enableDeployments` queue-time parameter. When it is `false` (the default),
they do not appear in the run at all. This avoids depending on runtime
boolean-parameter rendering in `condition:` expressions.

## Triggers and parameters

The pipeline runs on:

- pushes to `main`
- pull requests targeting `main`
- manual runs from Azure DevOps

Two queue-time parameters control optional work:

| Parameter | Default | Effect |
|---|---|---|
| `publishImages` | `false` | When true, the six images are built and pushed to Azure Container Registry with `Docker@2`, their digests are resolved, and the promoted digests are scanned. When false, they are built locally on the agent and that local build is scanned. |
| `enableDeployments` | `false` | When true, the Test, UAT, and Production stages are included. When false, they are omitted from the pipeline graph. |

A normal pull-request run uses the defaults and needs no Azure resources.
A release run sets **both** parameters to `true`: `publishImages` because the
deployment stages consume the `image-digests` artefact, and
`enableDeployments` because otherwise there is nothing to promote. Enabling
deployments without a published `image-digests` artefact fails at the download
step rather than deploying an unpublished build.

## Build stage

### `.NET` gates

The `DotNet` job runs on `ubuntu-latest` and performs:

1. `dotnet tool restore`
2. `dotnet build SmeDemo.slnx --configuration Release`
3. `dotnet test SmeDemo.slnx --configuration Release --no-build` with TRX
   results, published by `PublishTestResults@2`
4. `dotnet format SmeDemo.slnx --verify-no-changes`
5. `test -f openapi/v1.json` and `git diff --exit-code -- openapi/`
6. EF pending-model checks for the API and auth models
7. idempotent migration SQL regeneration and `diff` against `db/migrations/`
8. the MediatR/AutoMapper removal guard
9. `python3 tools/smoke/check-vulnerabilities.py`
10. a pinned, checksum-verified gitleaks full-history scan (checkout uses
    `fetchDepth: 0`)

The SDK is `dotnetVersion: 10.0.404`, kept in step with
[`global.json`](../global.json); the GitHub workflow reads that file directly
instead of repeating the version.

The formatting gate matches the repository's existing style through
[`.editorconfig`](../.editorconfig). It deliberately does not enforce a BOM
choice: the repository contains both BOM and BOM-less UTF-8 files, and an
explicit `charset` would rewrite dozens of files for no behavioural gain.

The OpenAPI check depends on the Release build regenerating the document. The
Release build does so through `Microsoft.Extensions.ApiDescription.Server`.
`openapi/v1.json` must be committed for the diff to have something to compare;
a stale document fails the job with a rebuild-and-commit message.

### Web job

The `Web` job uses Node 22, runs `npm ci`, `npm run build`, and `npm test` in
the `web/` directory.

### Compose validation

The `ComposeValidation` job copies `.env.example` to `.env` and runs
`docker compose config -q`. It also validates the deployment assets, so a broken
template or script fails in the Build stage rather than in the first deployment:

- `az bicep install` followed by
  `az bicep build --file infra/main.bicep --stdout > /dev/null`;
- `bash -n` over every `tools/deploy/*.sh` and `web/nginx/*.sh` script.

No Azure subscription is needed; Bicep compiles locally and `bash -n` only parses.

### Images

The `Images` job builds six images from the repository root:

| Image | Dockerfile | ACR repository when pushed |
|---|---|---|
| api | `src/SmeDemo.Api/Dockerfile` | `sme-demo/api` |
| auth | `src/SmeDemo.Auth/Dockerfile` | `sme-demo/auth` |
| web | `web/Dockerfile` | `sme-demo/web` |
| sms-mock | `tools/SmsMock/Dockerfile` | `sme-demo/sms-mock` |
| issuer-mock | `tools/IssuerMock/Dockerfile` | `sme-demo/issuer-mock` |
| migrator | `tools/Migrator/Dockerfile` | `sme-demo/migrator` |

Trivy is installed in both modes, pinned and checksum-verified like gitleaks:

| Variable | Value |
|---|---|
| `trivyVersion` | `0.76.2` |
| `trivySha256` | `bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf` |
| Linux x64 asset | `https://github.com/aquasecurity/trivy/releases/download/v0.76.2/trivy_0.76.2_Linux-64bit.tar.gz` |

**With `publishImages=true`**, each image is pushed with two tags:

- `$(Build.SourceVersion)` — the commit SHA, used for promotion and traceability
- `$(Build.BuildId)` — the Azure Pipelines run id, convenient for humans

After the six pushes, an `AzureCLI@2` step logs in with the ARM service
connection, runs `az acr login --name "$(acrName)"`, and calls
[`tools/deploy/resolve-digests.sh`](../tools/deploy/resolve-digests.sh) with
`--acr "$(acrName)" --tag "$(imageTag)"`. That script asks
`az acr repository show-manifests` for each repository and writes
`{"registry": "...", "tag": "...", "images": {"api": "...@sha256:...", ...}}`.
The directory is published as the `image-digests` pipeline artefact. Every
deployment stage downloads that artefact; none of them rebuilds or resolves a
tag again. Tags are for humans; the digest is the deployment contract.

The same job then generates one CycloneDX SBOM per **digest** and publishes them
as the `sboms` artefact, and runs the Trivy gate against each digest reference.
The registry credential from `az acr login` is still in the job's Docker config,
so Trivy pulls the promoted digests directly. The flags are the same as the
local path:

- `--severity HIGH,CRITICAL` with `--ignore-unfixed` and `--exit-code 1`, so a
  fixable high or critical finding fails the job before any environment can
  consume the digest;
- a 15-minute per-image timeout;
- each SBOM is generated before the gate, so a failed scan still leaves the
  inventory behind.

**With `publishImages=false`**, the job builds the same six images locally,
generates one CycloneDX SBOM per image, publishes them as `sboms`, and runs the
same Trivy gate against the local tags. That path proves the Dockerfiles and the
running image inventory on every pull request; it does not publish anything.

All six images currently scan clean locally under the same Trivy version and
flags. The repository uses Docker Official and Microsoft .NET 10 base images,
pinned by tag and digest; see
[Container image hardening](#container-image-hardening) for what is still open.

### Compose smoke test

The `ComposeSmoke` job:

1. copies `.env.example` to `.env`
2. runs `docker compose up -d --build` for `mssql`, `azurite`,
   `db-migrator`, `db-migrator-auth`, `db-grants`, `auth`, and `api`
   (Compose also starts their dependencies, including `sms-mock` and
   `issuer-mock`)
3. waits up to 180 seconds for `auth` and `api` to be healthy (the API
   healthcheck is `GET /readyz`) and for the three one-shot containers to exit 0
4. runs [`tools/smoke/compose-smoke.sh`](../tools/smoke/compose-smoke.sh)
5. dumps `docker compose ps -a` and `docker compose logs --no-color` on failure
6. always runs `docker compose down -v --remove-orphans`

The smoke script checks:

- `GET /healthz` on auth (`:8081`)
- `GET /readyz` on api (`:8082`), which must report 200 after checking SQL
  Server, Blob Storage, and auth metadata
- `POST /auth/login` with the demo credentials
- `POST /auth/challenges/<id>/verify` with the fixed demo OTP
- `GET /api/v1/me` with the access token: HTTP 200, `org` equal to the expected
  organisation (`sme` by default), and a non-empty `name`
- `GET /api/v1/me` without a token: HTTP 401 and `error.code` equal to
  `UNAUTHENTICATED`

It prints one `PASS:` or `FAIL:` line per check and exits non-zero if any check
fails. It needs only `curl` and `python3`; JSON is parsed with Python rather
than `jq` or brittle `grep`/`sed`.

A cold stack can take several minutes to start, mostly because SQL Server and
Azurite warm up and the migrators run before auth and api. The job timeout is
45 minutes.

### Dependency updates

Dependabot is not a pipeline job. [`.github/dependabot.yml`](../.github/dependabot.yml)
runs weekly on Monday and opens grouped pull requests for NuGet, npm under
`/web`, `github-actions`, and Docker in the root Compose file plus every
Dockerfile directory. Those pull requests then run through the same Build gates;
the digest-pinned base images move by Dependabot pull request rather than by a
manual edit. Docker major versions are ignored on purpose: a new .NET, Node or
nginx major changes the runtime contract and is handled as a separate work item,
while patch and digest updates inside the current major are still proposed.

## Deployment stages

The three deployment stages use Azure DevOps Environments:

| Stage | Environment | Gate |
|---|---|---|
| Deploy Test | `sme-demo-test` | automatic after a successful Build |
| Deploy UAT | `sme-demo-uat` | Environment approval/check; a human starts it |
| Deploy Production | `sme-demo-prod` | Environment approval/check, plus the business change ticket recorded in the run |

Each stage is a `deployment` job with a `runOnce` strategy, `ubuntu-latest`, a
60-minute timeout, and a checkout of the repository. UAT and Production have no
queue-time trigger of their own: the Environment approval is configured in Azure
DevOps, not in YAML, so changing an approver does not require a pipeline change,
and a release run moves through all three stages in order.

Each job:

1. binds its own variable group (`vg-sme-demo-test`, `-uat`, `-prod`) so the
   target resource group and credentials differ per stage;
2. downloads the `image-digests` artefact from the same run — no stage rebuilds
   or re-resolves a tag;
3. runs `bash tools/deploy/deploy.sh` under `AzureCLI@2` with
   `addSpnToEnvironment: true`, passing `ENVIRONMENT_NAME`, `RESOURCE_GROUP`,
   `LOCATION`, `ACR_NAME`, `NAME_PREFIX`, `IMAGES_JSON`, `SOURCE_COMMIT`,
   `DEPLOYMENT_RECORD_FILE`, the SQL and application-database credentials, the
   issuer key, and the demo values as environment variables;
4. attempts to publish the deployment record as `deployment-record-<env>` even
   when the deployment failed (`condition: succeededOrFailed()`), so a run that
   wrote the record before failing still leaves the attempted digests visible.

### What a deployment does

[`tools/deploy/deploy.sh`](../tools/deploy/deploy.sh) performs the same sequence
in every environment:

1. **Validate.** It requires `ENVIRONMENT_NAME` (`test`, `uat`, or `prod`),
   `RESOURCE_GROUP`, `LOCATION`, `ACR_NAME`, `IMAGES_JSON`, the SQL admin and
   application-database passwords, `ORG_ID`, `DEMO_MODE`, the demo credentials,
   and `ISSUER_API_KEY`. It reads `images.json`, requires every one of the six
   references to be pinned by `@sha256:`, and refuses to run when the registry is
   not in the target resource group.
2. **Deploy the template.** `az deployment group create` applies
   [`infra/main.bicep`](../infra/main.bicep) with the six digests, the SQL
   credentials, the issuer key, and the demo parameters. The template creates
   the Log Analytics workspace, Container Apps environment, storage account
   (Blob `card-art`, table `transactions`, Files share `auth-keys`), Azure SQL
   (`sme_api`, `sme_auth`), Key Vault (ten secrets), one user-assigned managed
   identity, five Container Apps, and three Container Apps Jobs.
3. **Run the one-shot jobs in order**:
   `<prefix>-<env>-job-db-grants` (server logins on `master`) →
   `-job-migrate-api` (API schema, then the `sme_api_app` user) →
   `-job-migrate-auth` (auth schema, then the `sme_auth_app` user). It starts
   each with `az containerapp job start`, polls the execution status, and fails
   on `Failed`, `Canceled`, or timeout with the exact
   `az containerapp job logs show` command to inspect it. Jobs do not retry
   themselves; the SQL is idempotent, so a fixed job is restarted, not re-run
   blind.
4. **Wait for revisions.** It restarts each app's latest revision after the jobs,
   so an app that started before its schema existed is not left in backoff, then
   waits for `web`, `auth`, `api`, `sms-mock`, and `issuer-mock` until
   `latestRevisionName` equals `latestReadyRevisionName` and `runningStatus` is
   `Running`. It does not call `/readyz` itself: internal ingress is unreachable
   from the agent, and the platform probes own container health.
5. **Record.** It prints and optionally writes a JSON deployment record with the
   environment, deployment name, resource group, public web URL, tag, commit,
   timestamp, and the six digest references. No password or connection string
   appears in it.
6. **Smoke the public path.** It exports `WEB_BASE_URL` from the template output
   and executes
   [`tools/deploy/environment-smoke.sh`](../tools/deploy/environment-smoke.sh)
   against the public web origin. Set `SKIP_SMOKE=true` only to recover an
   environment whose smoke is known to fail for an unrelated reason.

The environment smoke test checks, through the web origin only:

- `GET /` returns 200 HTML;
- `GET /config.js` returns 200 with `Cache-Control: no-store`;
- `POST /auth/login` returns 200 with a `challenge_id`;
- `POST /auth/challenges/<id>/verify` returns 200 with an `access_token`;
- `GET /api/v1/me` returns 200, `org` equal to `ORG_ID` (`sme`), and a non-empty
  `name`;
- anonymous `GET /api/v1/me` returns 401 with `error.code=UNAUTHENTICATED`.

### Container Apps mapping

| Compose service | Container Apps resource |
|---|---|
| `web` (nginx + SPA) | Container App, **external** ingress on 8080 |
| `auth` | Container App, **internal** ingress on 8081 |
| `api` | Container App, **internal** ingress on 8082 |
| `sms-mock` | Container App, internal ingress on 8083 |
| `issuer-mock` | Container App, internal ingress on 8084 |
| `db-migrator`, `db-migrator-auth`, `db-grants` | three Container Apps **Jobs** from the same migrator image |
| `mssql` | **Azure SQL** databases `sme_api` and `sme_auth` |
| `azurite` | **Azure Storage**: Blob container `card-art`, table `transactions` |
| `auth-keys` volume | **Azure Files** share `auth-keys`, mounted read-write at `/keys` on `auth` |

### Naming

| Object | Pattern | Example (Test) |
|---|---|---|
| Container App | `<namePrefix>-<env>-<service>` | `smedemo-test-web` |
| Container Apps Job | `<namePrefix>-<env>-job-<purpose>` | `smedemo-test-job-migrate-api` |
| Container Apps environment | `<namePrefix>-<env>-env` | `smedemo-test-env` |
| Resource group | created once by the operator | `rg-sme-demo-test` |
| Azure Container Registry | created once by the operator | `acrsmedemotest` |

`namePrefix` defaults to `smedemo` and is capped at 10 characters by the
template, because `-job-migrate-auth` and a four-character environment name must
still fit the 32-character Container App limit. The Log Analytics workspace, Key
Vault, SQL server, and storage account names are derived by the template from
the prefix, the environment, and a resource-group hash; `deploy.sh` prints the
SQL FQDN, and the template outputs expose the Key Vault and environment names.

### Probes

The platform probes replace the Compose in-image `HealthProbe`; the .NET runtime
images have no shell or `curl`.

| App | Ingress | Port | Probe path | Kind |
|---|---|---|---|---|
| `web` | external | 8080 | `/` | Liveness |
| `auth` | internal | 8081 | `/healthz` | Readiness and Liveness |
| `api` | internal | 8082 | `/readyz` (readiness), `/healthz` (liveness) | Readiness and Liveness |
| `sms-mock` | internal | 8083 | `/healthz` | Liveness |
| `issuer-mock` | internal | 8084 | `/healthz` | Liveness |

`api` readiness checks SQL, Blob Storage, and auth metadata; `auth` and the mocks
are live as soon as the process answers. All five apps run with one replica
until a shared rate limiter and shared readiness source exist (ADR-017).

### Rollback

[`tools/deploy/rollback.sh`](../tools/deploy/rollback.sh) moves an environment
back to the previous Container App revision:

```bash
tools/deploy/rollback.sh --environment test --resource-group rg-sme-demo-test
```

It lists revisions by creation time, activates the one before the newest, and
moves 100% of ingress traffic to it; `--app` limits the work to one app. This is
an application rollback only. Database migrations are forward-only (ADR-010):
the schema stays on the newer version, so the previous revision must still
tolerate it. If it does not, roll forward with a fixed image instead. Auth
signing keys stay on the `auth-keys` Azure Files share, so rolling `auth` back
does not invalidate tokens.

## Required Azure DevOps configuration

This is a one-time bootstrap, in order. It cannot be stored in Git because it
creates the subscription-level objects the pipeline depends on. The names below
are the ones the pipeline expects; substitute your own only if you also change
the matching variable.

### 1. Subscription, resource groups, and registry

- One resource group per environment, for example `rg-sme-demo-test`,
  `rg-sme-demo-uat`, and `rg-sme-demo-prod`, in the region set by `location`
  (`australiaeast` by default). Confirm the region before creating anything.
- An Azure Container Registry holding the six `sme-demo/*` repositories. The
  single-file template creates the `AcrPull` role assignment at the registry
  scope, so **the registry must be in the same resource group as the
  deployment**; `deploy.sh` checks this before doing anything and fails with an
  explanation if it is not. The registry name goes in `acrName`; the login
  server for image references comes from the `image-digests` artefact, so there
  is no separate variable to keep in step.

Every stage deploys the references in `images.json` unchanged, so all stages use
the registry that the Build stage pushed to. A separate registry per environment
would require the promoted digests to exist there and a rewrite step that this
repository does not implement; `deploy.sh` rejects an `images.json` whose `api`
reference is not from the expected registry rather than deploy the wrong image.

### 2. Service connections

- **Azure Resource Manager** (`azureServiceConnection`): authorised to deploy to
  every target resource group. ADR-020 names it `sc-sme-demo-azure`.
- **Docker registry** (`acrServiceConnection`): authorised to push to the six
  `sme-demo/*` repositories, used by `Docker@2`. ADR-020 names it
  `sc-sme-demo-acr`.

The principal behind the ARM connection also needs, on the target ACR, `AcrPush`
(for `az acr login` and the digest-bearing scan) and `AcrPull` (for
`az acr repository show-manifests`). `Docker@2` pushes with the separate Docker
registry service connection, not with this principal.

### 3. Environments and approvals

- `sme-demo-test` — no approval; Test deploys automatically after a green Build.
- `sme-demo-uat` — an approval/check. This is the manual gate; there is no
  separate queue-time trigger.
- `sme-demo-prod` — an approval/check, plus the business change ticket recorded
  in the run. Business-hours or exclusive-lock checks are optional additions.

### 4. Variable groups

Create `vg-sme-demo-test`, `vg-sme-demo-uat`, and `vg-sme-demo-prod`, and link
each to the matching pipeline stage. The pipeline reads these exact names:

| Variable | Required | Purpose |
|---|---|---|
| `sqlAdminPassword` | yes, secret | SQL Server administrator password; used by the migrator and bootstrap jobs only |
| `SME_API_DB_PASSWORD` | yes, secret | password for the `sme_api_app` login |
| `SME_AUTH_DB_PASSWORD` | yes, secret | password for the `sme_auth_app` login |
| `ISSUER_API_KEY` | yes, secret | shared key for `issuer-mock` |
| `testResourceGroup` / `uatResourceGroup` / `prodResourceGroup` | optional | override the pipeline default for that stage |
| `acrName` | optional | override the registry for that stage (see the single-registry caveat above) |
| `location` | optional | override the region for that stage |
| `namePrefix` | optional | override the resource-name prefix (10 characters maximum) |
| `azureServiceConnection` | optional | override the ARM connection for that stage |

Mark `sqlAdminPassword`, `SME_API_DB_PASSWORD`, `SME_AUTH_DB_PASSWORD`, and
`ISSUER_API_KEY` as secret variables. Link the group to Key Vault if the
organisation requires secret values to live there. None of these values may be
committed to this YAML or to `.env.example`; the pipeline-level demo values are
published by design and are not secrets.

The three database passwords are substituted into a T-SQL literal and into a
SqlClient connection string, so they must not contain a quote, semicolon,
backslash, backtick, or whitespace. `deploy.sh` rejects such a value before it
starts; generate them from a safe alphabet. Azure SQL's complexity rule is still
satisfiable without those characters.

The pipeline-level variables an operator normally adjusts are `acrName`,
`acrServiceConnection`, `azureServiceConnection`, `location`,
`sqlAdminLogin`, the three resource groups, and `namePrefix`. The demo
credentials (`orgId`, `demoMode`, `demoUsername`, `demoPassword`, `demoOtp`) are
published values; replace them and set `demoMode=false` for a non-demo
deployment.

### 5. Permissions of the deploying principal

The identity that runs the ARM deployment (the service-connection principal)
must be able to:

- create and update every resource in the target resource group (`Contributor`
  or a custom role with the resource-provider write actions);
- create role assignments — `Microsoft.Authorization/*/write` — because the
  template grants `AcrPull` to the managed identity at the registry scope and
  `Key Vault Secrets User` at the vault scope (`Owner` or `User Access
  Administrator` at the target scopes);
- write secret values — **Key Vault Secrets Officer** on each environment's
  vault, because the deployment creates the ten secrets. The first run creates
  the vault itself, so grant the role at the resource-group scope, or grant it
  on the new vault and redeploy once;
- read the Log Analytics shared key — the
  `Microsoft.OperationalInsights/workspaces/sharedKeys/action` permission, which
  is included in Log Analytics Contributor, because the Container Apps
  environment consumes `listKeys().primarySharedKey`.

Role assignments and Key Vault data-plane permissions can take several minutes
to propagate. If the first run fails while creating a revision or writing a
secret, re-run the deployment once propagation has completed; the template is
idempotent, so a redeploy does not create duplicates.

## Source repository

The pipeline is source-agnostic. It can use the existing GitHub repository
through an Azure Pipelines GitHub service connection, or an Azure Repos
repository after a migration. Keeping the code on GitHub is the lower-risk
initial choice; only the trigger repository configuration changes if the code
later moves.

## GitHub Actions and Azure Pipelines

Both pipelines run the same build gates during the transition:

| Concern | GitHub Actions | Azure Pipelines |
|---|---|---|
| PR feedback | `.github/workflows/ci.yml` | `Build` stage |
| Release/deployment | not present | Test/UAT/Prod stages calling `tools/deploy/deploy.sh` |
| Image publication | local build only | ACR push plus digest resolution via `publishImages` |
| Environment approvals | not present | Azure DevOps Environments |
| Secrets | GitHub secrets | variable groups / Key Vault |

Keeping both temporarily is intentional: GitHub Actions gives fast PR feedback
close to the code, while Azure Pipelines owns the formal release path. Once the
Azure pipeline is stable, the GitHub workflow can be reduced to the PR checks or
retired.

## Local reproduction

The build gates can be reproduced without Azure DevOps:

```bash
dotnet tool restore
dotnet build SmeDemo.slnx --configuration Release
dotnet test SmeDemo.slnx --configuration Release --no-build
dotnet format SmeDemo.slnx --verify-no-changes

python3 tools/smoke/check-vulnerabilities.py

curl -fsSL -o /tmp/gitleaks.tar.gz \
  https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz
echo "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  /tmp/gitleaks.tar.gz" \
  | sha256sum --check --strict
tar -xzf /tmp/gitleaks.tar.gz -C /tmp gitleaks
/tmp/gitleaks detect --source . --redact --config .gitleaks.toml --exit-code 1

docker compose config -q
az bicep install
az bicep build --file infra/main.bicep --stdout > /dev/null
for script in tools/deploy/*.sh web/nginx/*.sh; do bash -n "$script"; done

docker compose up -d --build mssql azurite db-migrator db-migrator-auth \
  db-grants auth api
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

The local Windows run used Docker Desktop's Linux engine and WSL bash. Example
timings with warm base images:

- format verification: 36 s
- Release build: 8 s incremental, 22 s first run
- Release tests: 29 s
- all six image builds: 7 min 19 s
- `docker compose up` to a healthy stack: 4 min 39 s
- smoke script: 5.5 s
- teardown: 17 s
- vulnerability scan of 17 projects: 77 s
- full-history gitleaks scan: 7 s

The Bicep build and the `bash -n` checks validate the deployment assets without
a subscription. What cannot be reproduced locally is everything that needs
Azure: `resolve-digests.sh` (ACR manifest lookup), `deploy.sh` (resource
deployment, Container Apps Jobs, revision readiness), `environment-smoke.sh`
against a public Container Apps origin, and `rollback.sh` (revision activation
and traffic weights). Those require a logged-in `az` session and a prepared
resource group; they are not simulated in CI.

Cold GitHub or Azure agents will be slower because base images and NuGet
packages are not cached.

## Security and quality notes

- **Formatting** is checked against `.editorconfig`; the release build treats
  warnings as errors, so style drift and compiler warnings fail the build.
- **OpenAPI** is a committed build artefact. The pipeline fails if the
  Release build rewrites it differently.
- **Migrations and SQL** are checked in both directions: pending model changes
  fail, and regenerated scripts must match the committed scripts byte for
  byte.
- **Removed packages** MediatR and AutoMapper fail the build if reintroduced.
- **Vulnerabilities** are scanned with `dotnet list package --vulnerable
  --include-transitive --format json`. The wrapper is required because the raw
  command exits 0 even when it reports findings.
- **Secrets** are scanned with gitleaks 8.30.1 over the full git history,
  downloaded from a pinned URL and verified against a SHA-256 checksum. The
  pipeline checks out with `fetchDepth: 0` so the scan covers every commit.
  `.gitleaks.toml` allowlists the documented Azurite emulator key and generated
  build output; it does not disable the default rule set.
- **Images** are scanned with Trivy 0.76.2 in both modes, pinned and
  checksum-verified. When the images are built locally (`publishImages=false`),
  the scan runs against the local tags. When they are pushed
  (`publishImages=true`), the tag is resolved to six digests and the scan runs
  against those **promoted digest references**, using the Docker credential that
  `az acr login` left in the job. Fixable `HIGH` and `CRITICAL` findings fail
  the build; `--ignore-unfixed` also filters unfixed findings out of the output,
  so this gate is not their inventory. A CycloneDX SBOM is generated for every
  image or digest and published as the `sboms` artifact before the gate runs.
- **Supply chain pinning** has two layers. The .NET SDK is pinned in
  `global.json` (`10.0.404`, `latestFeature`, no prereleases) for the GitHub
  workflow and repeated as `dotnetVersion` here; every base image is pinned by
  tag and digest in the Dockerfiles and `docker-compose.yml`.
- **Dependency updates** are proposed weekly by
  [`.github/dependabot.yml`](../.github/dependabot.yml), grouped per ecosystem
  for NuGet, npm under `/web`, `github-actions`, and Docker in the root Compose
  file plus every Dockerfile directory. Dependabot moves the tag and digest
  together; a digest-only change is how a rebuilt base image arrives.
- **No runtime dependency changes** were introduced for CI. The tooling is
  repository-local scripts plus Azure Pipelines tasks.

## Container image hardening

### What "Hardened Image" usually means

"Docker Hardened Images" (DHI) are a Docker-maintained catalog of minimal,
security-focused base images. Compared with Docker Official Images, the runtime
variants are designed to:

- contain only the runtime components, with no package manager and, for many
  variants, no shell
- run as a non-root user
- publish SBOMs (CycloneDX and SPDX) and SLSA Build Level 3 provenance
- be continuously rebuilt and patched; Docker's stated goal is near-zero known
  CVEs
- be available through the free DHI Community tier, with paid Select and
  Enterprise plans for compliance/SLA requirements

See [Docker Hardened Images](https://docs.docker.com/dhi/),
[DHI plans](https://docs.docker.com/subscription-billing/plans/dhi/), and the
[ASP.NET Core DHI catalog](https://hub.docker.com/hardened-images/catalog/dhi/aspnetcore).

"Harden Image" is not a standard Docker tag. Depending on where the label was
seen, it may mean:

- a DHI catalog repository such as `dhi.io/aspnetcore`
- a project-specific suffix such as `-dhi`, `-hardened`, `-distroless`, or
  `-chiseled`
- a registry or security-dashboard badge, not an image tag at all

A tag or label does not change what is inside an image. Before trusting a
"hardened" image, check its base image, digest, user, entrypoint, SBOM, and
provenance rather than the tag name.

### Current position

This demo currently uses Docker Official Images and Microsoft-published .NET
images, all pinned by tag and digest:

| Image | Runtime base | Hardening notes |
|---|---|---|
| api, auth, sms-mock, issuer-mock | `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` | no shell, package manager, or `curl`; runs as UID 1654 (`$APP_UID`); the Compose healthcheck is the in-image `dotnet /healthprobe/SmeDemo.HealthProbe.dll` probe; Compose adds a read-only root, dropped capabilities, `no-new-privileges`, a 64 MiB `/tmp`, and limits |
| migrator | `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra` | no shell or package manager; no healthcheck; one-shot process; same Compose hardening and limits |
| web | `nginx:1.31-alpine` | runs as the unprivileged `nginx` user on port 8080; `nginx-module-xslt` and `libxslt` are removed; the server block is rendered at container start from `UPSTREAM_AUTH`, `UPSTREAM_API`, and `NGINX_RESOLVER` into `/tmp/conf.d/default.conf`; still ships a shell and uses `wget` in the healthcheck; same hardening and limits |
| mssql, db-grants | `mcr.microsoft.com/mssql/server:2022-latest` | digest-pinned; mssql keeps `no-new-privileges` and CPU/memory limits only because its entrypoint must prepare the data volume; db-grants keeps the same two controls |
| azurite | `mcr.microsoft.com/azure-storage/azurite` | digest-pinned; same Compose hardening and limits as the application services |
| build stages only | `mcr.microsoft.com/dotnet/sdk:10.0`, `node:22-bookworm-slim` | not part of the runtime attack surface |

In the CLI Compose stack the runtime services run with a read-only root
filesystem, `cap_drop: [ALL]`, `no-new-privileges:true`, and a 64 MiB `tmpfs` at
`/tmp`; the migrators and `db-grants` keep their one-shot behaviour. SQL Server
and `db-grants` are the documented exception to the read-only/capability drop.
`docker-compose.vs.debug.yml` relaxes `read_only` and adds `SYS_PTRACE` for the
Visual Studio debugger only, and `docker compose up` never reads that file.

The .NET runtime stages are Microsoft chiseled images: no shell, no package
manager, and no `curl`. They use the `-extra` variant because
`Microsoft.Data.SqlClient` 6.1.7 rejects invariant globalization; the standard
chiseled variant has no ICU and cannot be used by api, auth, or the migrator.
`-extra` adds ICU/tzdata but still has no shell or package manager. The Compose
healthcheck is a small dependency-free .NET console app (`tools/HealthProbe`)
built in an SDK stage and copied to `/healthprobe`; it performs the same
`GET /healthz` / `GET /readyz` check and success/failure outcome that
`curl -fsS` used to. The auth image creates `/keys` in an SDK stage and copies
it with the application user's ownership, so no runtime `chown` or shell is
needed. The migrator also has no shell, but needs no healthcheck.

Because `/bin/sh` is absent from these images, `docker compose exec <service> sh`
is not available for `api`, `auth`, `sms-mock`, `issuer-mock`, or the migrator.
Use `dotnet`, `docker inspect`, the container logs, or copies out of the
container instead; writing into a read-only-root container is refused. See
[`development.md#container-runtime-hardening`](development.md#container-runtime-hardening).
`web` remains the one runtime image with a shell and `wget`.

DHI is **not required for the demo**, and it is not a drop-in replacement for
the current Dockerfiles:

- The .NET images already use Microsoft's chiseled variant, which removes the
  shell and package manager while remaining continuously patched and
  digest-pinned. DHI would add Docker-maintained patching, SBOM, and provenance
  on top of that, but requires registry access/terms and a different tag and
  image layout.
- The web image's entrypoint script and healthcheck still assume a shell and
  `wget`; any replacement must preserve the unprivileged `nginx` user, port 8080,
  and the `/tmp/config.js` path.
- The migrator has no healthcheck, but its chiseled base and certificate/runtime
  dependencies still need validation against the target SQL Server.
- The .NET DHI examples use different tags and image layouts; a migration must
  be tested with the real Compose stack and the smoke script.

### Recommendation

For an Azure production landing, the container hardening work item is mostly
closed for the .NET services, but the platform and web image still need work:

1. **Done — hardened .NET base images.** The API, auth, both mocks, and the
   migrator use Microsoft chiseled images. DHI Community remains an evaluation
   item ([ADR-019](reference/adr/ADR-019-chiseled-runtime-images-and-health-probe.md)),
   not a current dependency.
2. **Done — multi-stage builds.** Only the runtime stage ships; the SDK, probe
   build stage, and Node stages stay out of the final image.
3. **Done for .NET — remove shell/`curl` from the runtime image.** The
   in-image .NET probe replaces `curl`; the images have no `/bin/sh`. **Open for
   `web`:** nginx still needs a shell and `wget`, so removing them requires a
   shell-free web image or an external probe for that one service.
4. **Done in Compose and the .NET images, partly done on the platform.** The
   .NET services and nginx run as non-root, and the CLI stack uses a read-only
   root filesystem, `no-new-privileges`, and dropped capabilities. The Container
   Apps template relies on the image's non-root user and platform isolation; it
   does not yet express a read-only root filesystem or capability drops in the
   container definition. The platform probes call the HTTP endpoints directly
   instead of exec-ing the in-image probe.
5. **Done — digest pinning, image scanning, and SBOMs.** Base images are pinned
   by tag and digest, Dependabot updates them weekly, and CI scans the built
   images and publishes CycloneDX SBOMs. Provenance/SLSA attestation remains
   open.
6. **Ongoing — re-run the Compose smoke test and the environment smoke tests
   after every base-image change;** health-check behaviour is the most likely
   regression.

Do not add a decorative "harden" tag or label. Hardening is a property of the
image contents and runtime configuration, not of its name.

## Troubleshooting

| Symptom | Cause / resolution |
|---|---|
| `docker-compose.dcproj` cannot be opened during `dotnet format` | Expected warning: the Visual Studio Compose project has no associated language. `dotnet format` still exits 0. No exclusion is needed. |
| OpenAPI check is vacuous or reports the file missing | `openapi/v1.json` is a committed artefact. If it is absent, regenerate it with `dotnet build src/SmeDemo.Api/SmeDemo.Api.csproj --configuration Release` and commit it; without the file the `test -f` step fails before the diff runs. |
| `check-vulnerabilities.py` fails with a restore error | The script uses `--no-restore`; run the Release build first so `obj/project.assets.json` exists. |
| gitleaks reports the Azurite key | The well-known emulator key is allowlisted by `.gitleaks.toml`. Any other finding should be treated as real. |
| Compose smoke times out on a cold agent | SQL Server and Azurite startup dominates. The wait loop allows 180 seconds and the job allows 45 minutes. |
| ACR push or digest resolution fails | Check the Docker registry service connection (`acrServiceConnection`) and the ARM connection (`azureServiceConnection`); the ACR must exist and both principals need `AcrPush`, with `AcrPull` for `show-manifests`. The tag must have been pushed by the same run. |
| Deployment stage fails before any Azure call | `enableDeployments=true` but `publishImages=false`, so the `image-digests` artefact does not exist; the download step fails. Set both parameters for a release run. |
| Deployment fails because the ACR is not in the target resource group | `deploy.sh` refuses to run by design: the single-file template can only create the `AcrPull` role assignment when the registry and the deployment share a resource group. Move the registry or change the target group. |
| A new revision cannot pull the image | `AcrPull` on the user-assigned identity was just created and has not propagated, or the digest is not in that registry. Re-run the deployment after a few minutes; confirm the digest exists with `az acr repository show-manifests`. |
| `auth` cannot write `/keys` | The Azure Files share is mounted but not writable by the non-root auth user. Check `az containerapp logs show --name <prefix>-<env>-auth --resource-group <rg> --tail 100`. The ADR-020 fallback is a single auth replica with no mount, accepting token reset per revision. |
| A migrator job fails | Inspect the execution with `az containerapp job logs show --name <prefix>-<env>-job-migrate-api --resource-group <rg> --execution <id> --container migrator`. The SQL is idempotent; fix the cause and restart the job. |
| A Container App never becomes ready | Check `az containerapp revision list --name <app> --resource-group <rg> -o table` and `az containerapp logs show --name <app> --resource-group <rg> --tail 100`. Internal ingress means the agent cannot call `/readyz`; the revision state and the platform probe reason are the evidence. |
| Bicep validation fails on a local shell | `az bicep install` then `az bicep build --file infra/main.bicep --stdout` reproduces it; the Build stage runs the same two commands. |

## Decisions and remaining gaps

Decisions 1–5 of the previous version are resolved by
[ADR-020](reference/adr/ADR-020-azure-hosting-and-test-deployment.md):

1. Hosting is Azure Container Apps with Azure SQL, Blob/Table Storage, Azure
   Files, Key Vault, and a managed identity.
2. Test deploys automatically after a green `main` build when
   `enableDeployments=true`; UAT is manual through the `sme-demo-uat`
   Environment approval.
3. Production uses the `sme-demo-prod` Environment approval plus the business
   change ticket recorded in the run.
4. The code stays on GitHub; only the trigger configuration changes if it moves.
5. GitHub Actions stays the PR gate. Reducing or retiring it is a later
   decision after the Azure line has run green for several releases.

The remaining work is production hardening, not a decision about this
implementation:

1. **Private networking.** The demo has no VNet integration or private
   endpoints; Azure SQL has the `0.0.0.0` "Allow Azure services" rule, and Key
   Vault and Storage have public endpoints. A production landing needs a VNet,
   private DNS, private endpoints, and removal of the public SQL rule.
2. **High availability and backups.** One replica per service, one SQL database
   per app, no zone redundancy, no autoscaling, and no tested backup/restore.
   Production needs multi-replica-safe state (see 4), managed backups with
   point-in-time recovery, an RPO/RTO, and a restore exercise.
3. **DHI and the web shell.** The .NET images are chiseled; `web` still ships a
   shell and `wget`, and the nginx entrypoint renders its configuration at
   start. Removing them requires a shell-free web image or an external probe.
4. **Shared rate limiter.** ADR-017's limits, challenge store, and readiness
   cache are per process, which is why every app runs one replica. A shared
   limiter and shared readiness source are prerequisites for scaling.
5. **Deprecation headers.** ADR-012 keeps `/api/v1` with a documented
   deprecation window but no `Deprecation`/`Sunset` headers are emitted yet;
   add them before the first API version transition.
