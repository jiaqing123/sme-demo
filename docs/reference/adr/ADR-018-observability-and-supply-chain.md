# ADR-018 — Observability and supply chain: opt-in telemetry, scanned images, hardened runtime

- **Status:** Accepted
- **Date:** 2026-11-14
- **Owner:** Architecture
- **Deciders:** Architecture, Security, Platform

## Context

The runtime baseline now has to answer three operational questions that the
earlier decisions did not cover:

1. What evidence exists when a request fails, without turning the offline demo
   into a telemetry-dependent system?
2. How are the toolchain and the shipped image inventory kept pinned, scanned,
   and reproducible?
3. How does the Compose runtime reduce its attack surface without making local
   debugging impossible?

At the same time, readiness grew a cache and stricter metadata validation. The
demo still runs as one replica of each service on one machine, ships no
collector, and has no managed backup or key service. Without a recorded
decision, a later change could enable an exporter by default, scan the wrong
artefact, or relax the container hardening to make a debugger work and leave
that relaxation in the CLI stack.

## Decision

### 1. Observability is opt-in, correlated, and deliberately narrow

- Both independent hosts (`sme-demo-api` and `sme-demo-auth`) compose
  OpenTelemetry in their own `Observability/Telemetry.cs`. Tracing covers
  ASP.NET Core requests, outgoing `HttpClient` calls, EF Core, and the
  `Azure.Storage.Blobs` / `Azure.Data.Tables` activity sources. Metrics cover
  ASP.NET Core, `HttpClient`, and .NET runtime metrics (GC, exceptions, thread
  pool, CPU, working set).
- Every span carries `service.name`, `service.version` (assembly informational
  version without `+<sha>` build metadata), `service.instance.id` (`HOSTNAME`
  when set, otherwise a random process GUID), and
  `deployment.environment.name` (the ASP.NET Core environment, lower-cased).
- Exporters are opt-in. `OTEL_EXPORTER_OTLP_ENDPOINT` exports traces and
  metrics; `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` exports traces only and takes
  precedence for traces. `OTEL_CONSOLE_EXPORT=true` adds the console exporter.
  With no endpoint and no console flag, instrumentation runs and the signals are
  dropped. The host never defaults to `localhost:4317`.
- Health probes are excluded from tracing: `/healthz` and `/readyz` are
  infrastructure noise, not user requests.
- EF Core query text and parameters are stripped before export. The
  instrumentation callback clears `db.statement`, `db.query.text`, and
  `db.stored_procedure.name`; query parameters stay off. Spans keep timing,
  provider, and outcome.
- Console logging switches to the JSON formatter only when `LOG_FORMAT=json`
  (case-insensitive). JSON logging includes scopes, UTC millisecond timestamps,
  and `TraceId` / `SpanId`; the default simple console format also tracks both
  ids.
- A first-in-pipeline middleware owns `X-Request-Id` in both hosts. An inbound
  value is accepted only when it is 1–64 characters from
  `[A-Za-z0-9._:-]`; otherwise the current trace id (or a GUID when no activity
  exists) is used. The chosen value becomes `HttpContext.TraceIdentifier`, is
  tagged on the span as `request.id`, is written to the response before the
  pipeline runs, and is re-asserted in `OnStarting` so error envelopes that
  reset the response still carry it. It is logged as the scope key
  `request_id`.

### 2. Supply chain: pin the toolchain, pin the images, scan what ships

- The .NET SDK is pinned in `global.json` to `10.0.404` with
  `rollForward: latestFeature` and `allowPrerelease: false`. CI reads the same
  file, so the agent and the developers use one toolchain; the Dockerfiles build
  with the digest-pinned `dotnet/sdk` image instead.
- Dependabot covers NuGet, npm, `github-actions`, and every Dockerfile/Compose
  directory (the root Compose file plus `src/SmeDemo.Api`, `src/SmeDemo.Auth`,
  `web`, `tools/IssuerMock`, `tools/SmsMock`, and `tools/Migrator`). Updates are
  weekly on Monday and grouped so a patch wave produces a small, fixed number of
  pull requests per ecosystem instead of one per package.
- Every base image is pinned by tag and digest, in the Dockerfiles and in
  `docker-compose.yml`. Dependabot moves the tag and the digest together; a
  digest-only change is how a rebuilt base image arrives.
- CI pins Trivy `0.76.2` by SHA-256, downloads and verifies the release, and
  scans every image that the build produces (api, auth, web, sms-mock,
  issuer-mock, migrator). Fixable `HIGH` and `CRITICAL` findings fail the build
  (`--severity HIGH,CRITICAL --ignore-unfixed --exit-code 1`). CycloneDX SBOMs
  are generated for the same images before the gate and uploaded as build
  artefacts.
- The existing gates stay: gitleaks scans the working tree with a pinned,
  checksum-verified binary, and `tools/smoke/check-vulnerabilities.py` scans the
  NuGet graph for advisories. Trivy is added because the image, not the NuGet
  graph, is what is promoted; OS packages and published application libraries
  are in scope only at the image layer.

### 3. The Compose runtime is hardened by default

- The shared Compose hardening anchor applies a read-only root filesystem,
  `no-new-privileges:true`, `cap_drop: [ALL]`, and a 64 MiB `tmpfs` at `/tmp` to
  the .NET services and to nginx. Each service also declares CPU and memory
  limits. SQL Server keeps only `no-new-privileges` plus its limits because its
  entrypoint must prepare the data volume.
- The web runtime is `nginx:1.31-alpine` run as the unprivileged `nginx` user on
  port 8080. `nginx-module-xslt` and `libxslt` are removed at build time because
  the configuration never loads XSLT; this is also why all six images currently
  scan clean. `/config.js` is generated into `/tmp` (the root filesystem is
  read-only) and served with `Cache-Control: no-store`.
- Visual Studio debug overrides live in `docker-compose.vs.debug.yml`, which a
  plain `docker compose up` never reads. They relax `read_only` and add
  `SYS_PTRACE` for `api` and `auth` only, because a debugger needs a writable
  filesystem and ptrace. The CLI stack stays hardened.
- Kestrel enforces an explicit request-body ceiling through
  `MAX_REQUEST_BODY_BYTES`, defaulting to 1 MiB to match nginx's
  `client_max_body_size 1m`. An invalid value fails startup rather than silently
  falling back. TestServer does not apply Kestrel limits, so the contract tests
  cover the 413 envelope while Kestrel owns the runtime gate.

### 4. Readiness is cheap to poll and strict about auth metadata

- `ReadinessService` caches a snapshot for 2 seconds and makes refreshes
  single-flight, so Compose, an orchestrator, and a human with `curl` share one
  downstream check rather than multiplying SQL Server, Blob Storage, and auth
  calls. The 5 second overall budget and the `503` + `Retry-After: 5` contract
  are unchanged from ADR-017.
- The auth metadata probe validates more than a `200`: the document must be a
  JSON object, its `issuer` must match the configured `JWT_ISSUER` (trailing
  slash normalised), and its `jwks_uri` must be an absolute `http(s)` URL. When
  `AUTH_REQUIRE_HTTPS_METADATA` is true, `jwks_uri` must be `https`. A proxy
  error page, a partial document, or metadata for another issuer is a readiness
  failure, not a valid document.

## Consequences

- A normal demo run has no telemetry dependency and no additional container.
  The corollary is that there is no trace or metric evidence after an incident
  unless an endpoint or the console exporter was enabled before it happened.
- Telemetry remains per instance, as do the rate limits recorded in ADR-017.
  There is no collector, no aggregation across replicas, no sampling policy, and
  no shared metrics store.
  The 2 second readiness cache is also per process; a recovered dependency can
  take up to that window to appear ready, and a just-failed dependency can
  appear ready for up to the same window.
- Readiness bodies stay safe to expose: they carry dependency names and states,
  never exception text or connection strings. A mismatch between `JWT_ISSUER`
  and the auth document now fails readiness before it becomes a stream of
  misleading `401`s.
- The hardened runtime constrains what a container can write. Any new service
  that needs scratch space must use `/tmp` or a volume, and any new writable
  path must be added deliberately. Debugging is the documented exception:
  `docker-compose.vs.debug.yml` is less hardened than the CLI stack.
- Image scanning is only as complete as the image inventory and the digest
  pins. Unfixed base-image CVEs are reported but do not fail the build; if a
  fixable finding has to be accepted, it must be recorded in a time-boxed
  `.trivyignore.yaml` with an expiry and an owner, and revisited before that
  date. No ignore file is needed today: all six images scan clean. An ignore
  without an expiry is not an accepted risk.
- The `1 MiB` body ceiling is a product limit as well as a hardening control.
  A future upload feature must raise `MAX_REQUEST_BODY_BYTES` and the nginx
  limit together, or it will fail with `413`.
- **A production deployment must change:**
  - add a collector and a sampling policy, and export logs centrally instead of
    leaving them on the console;
  - keep the SDK, image digests, and scanner pins current through the weekly
    Dependabot pull requests, and run the image scan on the promoted digest;
  - replace the in-memory readiness cache and limiters with shared state when
    running more than one replica;
  - use a managed secret store and a managed HSM/KMS for the signing keys
    instead of `.env` and the `auth-keys` volume;
  - keep the read-only root filesystem and dropped capabilities, and add the
    platform's own pod/container security context rather than copying Compose;
  - provide managed backups, point-in-time recovery, and tested restores, which
    the demo does not have;
  - terminate TLS at the ingress and stop publishing direct host ports to
    `auth` or `api`.

## References

- [`../../observability.md`](../../observability.md) — signals, resource
  attributes, request-id correlation, and the local collector recipe
- [`../../development.md#continuous-integration`](../../development.md#continuous-integration)
  — the build, OpenAPI, dependency, and secret gates
- [`../../azure-pipelines.md#container-image-hardening`](../../azure-pipelines.md#container-image-hardening)
  — the hardening programme and its open work items
- [`../../runbook.md`](../../runbook.md) — incident, rotation, rollback, and
  observability operations
- [`ADR-017`](ADR-017-per-instance-rate-limiting-and-readiness.md) — the
  per-instance readiness and rate-limiting decisions this record extends
- Implementation:
  [`Telemetry.cs` (API)](../../../src/SmeDemo.Api/Observability/Telemetry.cs),
  [`Telemetry.cs` (auth)](../../../src/SmeDemo.Auth/Observability/Telemetry.cs),
  [`RequestIdMiddleware.cs` (API)](../../../src/SmeDemo.Api/Http/RequestIdMiddleware.cs),
  [`RequestIdMiddleware.cs` (auth)](../../../src/SmeDemo.Auth/Http/RequestIdMiddleware.cs),
  [`ReadinessService.cs`](../../../src/SmeDemo.Api/Readiness/ReadinessService.cs),
  [`AuthMetadataReadinessProbe.cs`](../../../src/SmeDemo.Api/Readiness/AuthMetadataReadinessProbe.cs),
  [`RequestBodyLimit.cs`](../../../src/SmeDemo.Api/Configuration/RequestBodyLimit.cs)
- Configuration and delivery:
  [`global.json`](../../../global.json),
  [`dependabot.yml`](../../../.github/dependabot.yml),
  [`ci.yml`](../../../.github/workflows/ci.yml),
  [`docker-compose.yml`](../../../docker-compose.yml),
  [`docker-compose.vs.debug.yml`](../../../docker-compose.vs.debug.yml),
  [`web/Dockerfile`](../../../web/Dockerfile), and
  [`web/nginx/default.conf`](../../../web/nginx/default.conf)
