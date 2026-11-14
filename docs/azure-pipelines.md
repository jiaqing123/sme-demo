# Azure Pipelines Guide

This document describes the Azure Pipelines delivery pipeline in
[`azure-pipelines.yml`](../azure-pipelines.yml). It complements the GitHub
Actions workflow in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml):
GitHub Actions remains the fast pull-request feedback loop, while Azure
Pipelines is intended to become the formal build, environment promotion, and
release pipeline. The cadence, promotion-by-digest rules, and release checklist
are in [`delivery.md`](delivery.md); this document describes the pipeline wiring.

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
 ├─ DotNet            build, tests, format, OpenAPI, EF/SQL, package and secret gates (SDK pinned to global.json)
 ├─ Web               npm ci, build, test
 ├─ ComposeValidation docker compose config -q
 ├─ Images            build six images; generate SBOMs and scan them; optionally push to ACR
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

With `publishImages=false` the job also scans what it built. It generates one
CycloneDX SBOM per image, publishes them as the `sboms` pipeline artifact, and
then runs the Trivy vulnerability gate on each image:

- `--severity HIGH,CRITICAL` with `--ignore-unfixed` and `--exit-code 1`, so a
  fixable high or critical finding fails the job;
- a 15-minute per-image timeout;
- each SBOM is generated before the gate, so a failed scan still leaves the
  inventory behind.

The scan steps are conditional on `publishImages=false` because `Docker@2`
pushes without keeping local image tags on the agent; when images are pushed to
ACR, the promoted digest should be scanned by the registry-side gate instead.
Trivy is pinned and checksum-verified like gitleaks:

| Variable | Value |
|---|---|
| `trivyVersion` | `0.76.2` |
| `trivySha256` | `bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf` |
| Linux x64 asset | `https://github.com/aquasecurity/trivy/releases/download/v0.76.2/trivy_0.76.2_Linux-64bit.tar.gz` |

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
- Release tests: 29 s
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
- **Images** are scanned with Trivy 0.76.2 when they are built locally
  (`publishImages=false`), also pinned and checksum-verified. Fixable `HIGH` and
  `CRITICAL` findings fail the build; `--ignore-unfixed` also filters unfixed
  findings out of the output, so this gate is not their inventory. A CycloneDX
  SBOM is generated for every image and published as the `sboms` artifact before
  the gate runs. When images are pushed to ACR, these
  local scan steps do not run; the promoted digest still needs a registry-side
  scan.
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
| web | `nginx:1.31-alpine` | runs as the unprivileged `nginx` user on port 8080; `nginx-module-xslt` and `libxslt` are removed; still ships a shell and uses `wget` in the healthcheck; same hardening and limits |
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
4. **Done in Compose and the .NET images, open on the platform.** The .NET
   services and nginx run as non-root, and the CLI stack uses a read-only root
   filesystem, `no-new-privileges`, and dropped capabilities. A deployment must
   still reproduce the same security context in its pod or container definition,
   and orchestrator probes should call the HTTP endpoints directly instead of
   exec-ing the in-image probe.
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
