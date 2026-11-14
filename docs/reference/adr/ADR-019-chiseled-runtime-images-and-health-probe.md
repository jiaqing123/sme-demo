# ADR-019 — Chiseled runtime images and an in-image .NET health probe

- **Status:** Accepted
- **Date:** 2026-11-14
- **Owner:** Architecture
- **Deciders:** Architecture, Security, Platform

## Context

ADR-018 hardened the Compose runtime but left the .NET images on
`mcr.microsoft.com/dotnet/aspnet:10.0` and `runtime:10.0`. Those images ship a
shell and a package manager, and the API, auth, and both mock services installed
`curl` only so a Compose healthcheck could run `CMD-SHELL curl -fsS ...`. The
delivery chain therefore still carried two avoidable pieces of attack surface:
an interactive shell that nothing at runtime needs, and a network client that is
not part of the application.

The hardening tracker in
[`docs/azure-pipelines.md#container-image-hardening`](../../azure-pipelines.md#container-image-hardening)
listed three open items: a hardened base image, removal of the shell and `curl`,
and a replacement for the shell-based healthcheck. DHI (Docker Hardened Images)
was one candidate, but it requires a registry account/terms and a different image
layout, and it is not required to close the local demo's gap.

At the same time, Compose healthchecks must run inside the container. Moving the
check to an external process or an orchestrator probe would break
`depends_on: condition: service_healthy` and the single-command local stack, which
is the deployment unit recorded in requirements §3 and §10.6. The replacement
must therefore be a binary that already exists in the runtime image.

## Decision

### 1. .NET runtime stages use Microsoft's chiseled Ubuntu images

- `api`, `auth`, `sms-mock`, and `issuer-mock` run on
  `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`; the one-shot
  migrator runs on
  `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra`. Both are pinned
  by manifest-list digest and updated by Dependabot like every other base image.
- The SDK stage stays `mcr.microsoft.com/dotnet/sdk:10.0`. Only the build stage
  has a shell or a package manager; the final image does not.
- The `-extra` variant is required, not optional. `Microsoft.Data.SqlClient`
  6.1.7 refuses to run when `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` and
  fails with `Globalization Invariant Mode is not supported`. The standard
  chiseled image sets that variable and ships no ICU; forcing the variable to
  `false` fails with `Couldn't find a valid ICU package installed`. The `-extra`
  variant includes ICU and tzdata, leaves globalization enabled, and still has
  no shell, package manager, or `curl`. All five .NET images use it so the
  services do not drift into different culture behaviour.
- No `apt-get`, no shell wrapper, and no `curl` is added to any runtime stage.

### 2. Compose healthchecks use a small .NET probe

- `tools/HealthProbe` is a dependency-free console application,
  `SmeDemo.HealthProbe`. It takes exactly one absolute HTTP(S) URL, issues a
  `GET` with a 5 second timeout, and exits `0` for a 2xx response, `1` for a
  non-2xx response or transport failure, and `2` for a usage error.
- Compose invokes it in exec form:
  `["CMD", "dotnet", "/healthprobe/SmeDemo.HealthProbe.dll", "<url>"]`. No shell
  is involved, so the healthcheck is one process with a zero/non-zero result,
  the same healthcheck semantics as the old `curl -fsS` (the numeric code is
  different, but Compose only cares whether it is zero).
- Each Dockerfile builds the probe in an SDK stage and copies it to
  `/healthprobe`. For `api` and `auth` the copy is in the `base` stage, because
  Visual Studio Fast mode builds only `base` and still applies the Compose
  healthcheck; `final` inherits it. The mock images copy it into their runtime
  stage.
- The probe is a healthcheck tool, not an application dependency. It is not
  referenced by any service and is not part of the request path.

### 3. Writable paths are prepared without a runtime shell

- Auth creates `/keys` in an SDK stage and copies it into `base` with
  `--chown=$APP_UID:$APP_UID`. Docker initialises the `auth-keys` named volume
  from that directory, so the non-root application can write signing keys
  without a runtime `chown`.
- `/tmp` remains the only writable scratch path in the CLI stack, supplied by
  the Compose tmpfs. The probe does not write to disk.

### 4. The exception is explicit

- `web` remains `nginx:1.31-alpine` with its shell and `wget` healthcheck. Its
  entrypoint script and runtime-config script depend on a shell, and replacing
  the nginx image is a separate change with its own configuration contract.
- `mssql`, `db-grants`, and `azurite` are unchanged: they are vendor images whose
  entrypoints and healthchecks need their packaged tools.
- DHI is not adopted in this change. It remains a candidate for a production
  landing when registry access, patching terms, and image layout have been
  evaluated. The chiseled images close the shell/`curl` gap without that
  dependency.

## Alternatives considered

| Alternative | Why it was not chosen |
|---|---|
| Keep `aspnet:10.0` and `curl` | Keeps a shell, a package manager, and a network client in every image for a healthcheck that can be a ~100 KB managed executable. The image scan also keeps reporting packages the app never uses. |
| Use the standard `-chiseled` variant | Smaller, but incompatible with `Microsoft.Data.SqlClient`, which rejects invariant globalization. The migrator, api, and auth cannot connect to SQL Server on that base. |
| Use DHI Community now | Requires a Docker account/terms and different tags and layouts; the benefit over Microsoft's continuously patched chiseled images is not material for this demo. Recorded as a separate evaluation. |
| Run healthchecks from an external probe container or the host | Breaks the self-contained Compose contract, `depends_on: service_healthy`, and the existing single-command CI smoke job. Orchestrator-level probes are still recommended in production and can call the same HTTP endpoints directly. |
| Keep `CMD-SHELL` and add a static `curl` binary | Still ships a network client and a shell-compatible layer; a static binary is no easier to explain or scan than the managed probe. |

## Consequences

- The .NET runtime images contain no shell, package manager, or `curl`.
  `docker compose exec api sh` no longer works; the documented checks use
  `docker inspect`, `docker compose logs`, copies out of the container, or
  `dotnet` directly. Writing into a read-only-root container is refused even
  under `/tmp`, so Docker's copy command is not a way to prove tmpfs
  writability. This is deliberate and is recorded in
  [`docs/development.md#container-runtime-hardening`](../../development.md#container-runtime-hardening).
- Debugging inside those containers is narrower. Visual Studio Fast mode still
  works because the `base` stage carries the probe, and the debugger attaches to
  `dotnet`; the shell is not part of that path.
- The health probe must track the same URLs and semantics as the endpoints
  (`/healthz` for auth, the mocks and web; `/readyz` for api). A new service with
  a Compose healthcheck needs the probe copied into its image, or must use a
  tool that already exists in its base image.
- Globalization stays enabled in the .NET containers (`-extra` provides ICU and
  tzdata), so the SQL client and any culture-aware code behave as they did on the
  full Ubuntu base. The price is a slightly larger image than the standard
  chiseled variant and a few ICU/tzdata packages in the scan surface.
- The real-stack smoke test is still what proves the login, OTP, card, artwork,
  migration, and readiness paths work on the new base; unit tests run on the host
  and would not catch a base-image-only failure such as the SqlClient invariant
  check.
- Image size and scan surface drop for the four app images and the migrator. The
  Trivy gate still runs on every image, and the nginx image remains the one
  runtime image with a shell.
- The base-image digests are part of the supply chain. Dependabot moves the
  chiseled tag and digest together; a major .NET upgrade remains a separate work
  item, as recorded in ADR-018.

## References

- [`../../azure-pipelines.md#container-image-hardening`](../../azure-pipelines.md#container-image-hardening)
  — the hardening programme and the DHI evaluation that remains open
- [`../../development.md#container-runtime-hardening`](../../development.md#container-runtime-hardening)
  — local verification without a shell
- [`../../requirements.md`](../../requirements.md) §10 — the Compose service and
  healthcheck contract
- [`ADR-018`](ADR-018-observability-and-supply-chain.md) — pinned base images,
  image scanning, and the Compose runtime controls this record builds on
- [`../../requirements.md`](../../requirements.md) §3, §10.6 — Compose is the
  deployment unit
- Implementation:
  [`tools/HealthProbe`](../../../tools/HealthProbe),
  [`docker-compose.yml`](../../../docker-compose.yml),
  [`Dockerfile` (API)](../../../src/SmeDemo.Api/Dockerfile),
  [`Dockerfile` (auth)](../../../src/SmeDemo.Auth/Dockerfile),
  [`Dockerfile` (sms-mock)](../../../tools/SmsMock/Dockerfile),
  [`Dockerfile` (issuer-mock)](../../../tools/IssuerMock/Dockerfile),
  [`Dockerfile` (migrator)](../../../tools/Migrator/Dockerfile)
