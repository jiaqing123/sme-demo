# Azure Pipelines Guide

This document describes the Azure Pipelines delivery pipeline in
[`azure-pipelines.yml`](../azure-pipelines.yml). It complements the GitHub
Actions workflow in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml):
GitHub Actions remains the fast pull-request feedback loop, while Azure
Pipelines is intended to become the formal build, environment promotion, and
release pipeline.

The Build stage is fully implemented and mirrors the GitHub Actions gates. The
Test, UAT, and Production stages are deliberately safe placeholders: they are
excluded unless explicitly enabled, and, once enabled, they fail until the real
migration and rollout steps are supplied. A deployment stage that does nothing
and reports success would be more dangerous than one that fails loudly.

## Pipeline at a glance

```text
trigger / PR
    │
    ▼
Build
 ├─ DotNet            build, tests, format, OpenAPI, EF/SQL, package and secret gates
 ├─ Web               npm ci, build, test
 ├─ ComposeValidation docker compose config -q
 ├─ Images            build six images; optionally push them to ACR
 └─ ComposeSmoke      start the minimal stack, wait, run the smoke script, tear down
    │
    ▼
Deploy Test           Environment: sme-demo-test
    │
    ▼
Deploy UAT            Environment: sme-demo-uat, optional approval
    │
    ▼
Deploy Production     Environment: sme-demo-prod, required approval
```

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
| `publishImages` | `false` | When true, the six images are built and pushed to Azure Container Registry with `Docker@2`. When false, they are built locally on the agent only. |
| `enableDeployments` | `false` | When true, the Test, UAT, and Production stages are included. When false, they are omitted from the pipeline graph. |

A normal pull-request run uses the defaults and needs no Azure resources.
A release run sets both parameters to `true` after the targets have been
configured.

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
10. a pinned, checksum-verified gitleaks working-tree scan

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
`docker compose config -q`. It needs Docker but no running services.

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

With `publishImages=true`, each image is pushed with two tags:

- `$(Build.SourceVersion)` — the commit SHA, used for promotion and traceability
- `$(Build.BuildId)` — the Azure Pipelines run id, convenient for humans

The same commit-SHA tag is intended to be promoted through Test, UAT, and
Production. Images must not be rebuilt per environment.

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

## Deployment stages

The three deployment stages use Azure DevOps Environments:

| Stage | Environment | Intended gate |
|---|---|---|
| Deploy Test | `sme-demo-test` | automatic after a successful Build |
| Deploy UAT | `sme-demo-uat` | optional approval/check on the environment |
| Deploy Production | `sme-demo-prod` | required approval/check; may also use business-hours or exclusive-lock checks |

Each stage is a `deployment` job with a `runOnce` strategy. The environment
approval is configured in Azure DevOps, not in YAML, so changing an approver
does not require a pipeline change.

### Placeholder behaviour

The deploy steps currently:

1. validate `acrLoginServer`, `azureServiceConnection`, and the matching
   `testResourceGroup` / `uatResourceGroup` / `prodResourceGroup` variable
2. print the target environment, resource group, and image reference
3. emit a `##vso[task.logissue type=error]` message
4. exit 1

This is intentional. Enabling deployments before the real steps exist must
fail, not silently report a release.

### What the real steps should do

Replace each placeholder with the same three logical operations, in this
order:

1. **Migrate** — run `sme-demo/migrator` (or an equivalent job) against the
   environment database, using a Key Vault-backed connection string. Apply the
   idempotent scripts before rolling out the application.
2. **Roll out** — deploy the image tag produced by the Build stage. Use the
   same digest/tag for every environment; do not rebuild during deployment.
3. **Smoke** — check auth's `/healthz` and the API's `/readyz`, then call the
   environment's auth and API URLs with the same contract as
   `compose-smoke.sh` and any environment-specific checks.

The rollout task depends on the hosting model:

| Hosting | Candidate tasks / commands |
|---|---|
| AKS | `KubernetesManifest@1` or `kubectl`; migrations as a Kubernetes Job |
| App Service | `AzureWebApp@1` / `AzureRmWebAppDeployment@4`; migrations via a container job or SQL step |
| Container Apps | `AzureCLI@2` with `az containerapp update --image`; migrations via a container job |

The hosting model is not yet chosen, which is why these stages are
placeholders.

## Required Azure DevOps configuration

Before setting `enableDeployments=true`, configure:

1. **Environments**
   - `sme-demo-test`
   - `sme-demo-uat` with an approval/check
   - `sme-demo-prod` with an approval/check

2. **Service connections**
   - a Docker registry service connection for the ACR
   - an Azure Resource Manager service connection for the target subscription

3. **Pipeline variables** (directly or via a variable group linked to Key Vault)

| Variable | Purpose |
|---|---|
| `acrLoginServer` | ACR login server used in deployment image references |
| `acrServiceConnection` | Docker registry service connection name |
| `azureServiceConnection` | Azure Resource Manager service connection name |
| `testResourceGroup` | target resource group for Test |
| `uatResourceGroup` | target resource group for UAT |
| `prodResourceGroup` | target resource group for Production |

Database passwords, JWT signing material, and other credentials belong in
Azure Key Vault or secret variables. They must not appear in this YAML or in
committed `.env` files.

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
| Release/deployment | not present | Test/UAT/Prod stages |
| Image publication | local build only | optional ACR push via `publishImages` |
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
/tmp/gitleaks detect --source . --no-git --redact --config .gitleaks.toml --exit-code 1

docker compose config -q
docker compose up -d --build mssql azurite db-migrator db-migrator-auth \
  db-grants auth api
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

The local Windows run used Docker Desktop's Linux engine and WSL bash. Example
timings with warm base images:

- format verification: 36 s
- Release build: 8 s incremental, 22 s first run
- Release tests: 29 s, 143 tests
- all six image builds: 7 min 19 s
- `docker compose up` to a healthy stack: 4 min 39 s
- smoke script: 5.5 s
- teardown: 17 s
- vulnerability scan of 16 projects: 77 s
- gitleaks scan: 2 s

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
- **Secrets** are scanned with gitleaks 8.30.1, downloaded from a pinned URL and
  verified against a SHA-256 checksum. `.gitleaks.toml` allowlists the
  documented Azurite emulator key and generated build output; it does not
  disable the default rule set.
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
images:

| Image | Runtime base | Hardening notes |
|---|---|---|
| api, auth, sms-mock, issuer-mock | `mcr.microsoft.com/dotnet/aspnet:10.0` | runs as non-root via `USER $APP_UID`; installs `curl` for the Compose healthcheck |
| migrator | `mcr.microsoft.com/dotnet/runtime:10.0` | no healthcheck; one-shot process |
| web | `nginx:1.27-alpine` | nginx master runs as root by default; uses `wget` in the healthcheck |
| build stages only | `mcr.microsoft.com/dotnet/sdk:10.0`, `node:22-bookworm-slim` | not part of the runtime attack surface |

DHI is **not required for the demo** and is not a drop-in replacement for the
current Dockerfiles:

- The API, auth, sms-mock, and issuer-mock images install `curl` with `apt-get`
  and use `CMD-SHELL curl` healthchecks. DHI runtime variants have no package
  manager or shell, so both would need to change.
- The web image's entrypoint script and healthcheck assume a shell and `wget`;
  the DHI nginx variant must be verified against the nginx configuration and
  runtime-config script.
- The migrator has no healthcheck, but its base image and certificate/runtime
  dependencies still need validation against the target SQL Server.
- The .NET DHI examples use different tags and image layouts; a migration must
  be tested with the real Compose stack and the smoke script.

### Recommendation

For an Azure production landing, add a separate **container hardening** work
item rather than folding it into the current CI change. The target state should
include:

1. Choose a hardened runtime base per service: DHI Community, Microsoft
   chiseled images, or an internally approved distroless base.
2. Keep multi-stage builds; only the runtime stage ships.
3. Remove shell/`curl` from the runtime image. Move health checks to an
   external probe or a supported health-check binary, and update Compose and
   the deployment probes together.
4. Run as non-root, with a read-only root filesystem, no-new-privileges, and
   dropped Linux capabilities where the hosting platform allows it.
5. Pin base images by digest, scan the built images (Microsoft Defender for
   Cloud, Trivy, or the ACR scanner), and publish SBOM/provenance.
6. Re-run the Compose smoke test and the environment smoke tests after the base
   image change; health-check behaviour is the most likely regression.

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
| Deployment stage fails immediately | The placeholder is still present, or `acrLoginServer` / `azureServiceConnection` / `*ResourceGroup` is unset. This is intentional until the real deployment is implemented. |
| ACR push fails | Check the Docker registry service connection and `acrServiceConnection`; the ACR must exist and the connection must be authorised. |

## Current open decisions

1. Hosting model: AKS, App Service, or Container Apps.
2. Whether Test deploys automatically on every successful `main` build and
   whether UAT is automatic or manual.
3. Whether Production uses only Environment approval or also a change ticket /
   business-hours check.
4. Whether the source stays on GitHub or moves to Azure Repos.
5. When to reduce or retire the GitHub Actions workflow after Azure Pipelines
   becomes the release source of truth.
