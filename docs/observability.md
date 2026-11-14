# Observability

How the API and auth service emit traces, metrics and logs, and how to turn
export on for a local investigation. The design rationale is in
[`ADR-018`](reference/adr/ADR-018-observability-and-supply-chain.md); this file
only says what exists and how to run it.

## What is emitted

Both hosts (`sme-demo-api` and `sme-demo-auth`) use the same composition, in
`src/SmeDemo.Api/Observability/Telemetry.cs` and
`src/SmeDemo.Auth/Observability/Telemetry.cs`.

| Signal | Instruments |
|--------|-------------|
| Traces | ASP.NET Core request spans (`/healthz` and `/readyz` are excluded), outgoing `HttpClient` calls, EF Core database spans, and the `Azure.Storage.Blobs` / `Azure.Data.Tables` activity sources |
| Metrics | ASP.NET Core request metrics, `HttpClient` metrics, .NET runtime metrics (GC, exceptions, thread pool, CPU, working set) |
| Logs | The normal console log in the existing simple format, or JSON when `LOG_FORMAT=json` |

Every span carries the usual resource attributes:

| Attribute | Value |
|-----------|-------|
| `service.name` | `sme-demo-api` or `sme-demo-auth` |
| `service.version` | The assembly informational version, without `+<sha>` build metadata |
| `service.instance.id` | `HOSTNAME` when set, otherwise a random GUID for the process |
| `deployment.environment.name` | The ASP.NET Core environment name, lower-cased (`development`, `production`) |

Exporters are opt-in. Instrumentation is always registered, but with no
endpoint configured the signals are collected and dropped. Nothing tries to
reach `localhost:4317` by default.

## Environment variables

| Variable | Default | Effect |
|----------|---------|--------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Sends traces **and** metrics to this OTLP endpoint. Required before any OTLP export starts. |
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | unset | Sends only traces to this endpoint and takes precedence over the generic endpoint for traces. Metrics stay disabled unless `OTEL_EXPORTER_OTLP_ENDPOINT` is also set. |
| `OTEL_CONSOLE_EXPORT` | unset | When `true` (case-insensitive), also writes every span and metric batch to the console. Intended for short local checks, not a running Compose stack. |
| `LOG_FORMAT` | unset (simple console) | When `json` (case-insensitive), switches to the JSON console formatter with UTC timestamps at millisecond precision and scopes included. |
| `X-Request-Id` | absent | Optional request header, not an environment variable. See below. |

The OTLP exporter is the standard OpenTelemetry .NET one. This repository
decides whether to add it and passes the configured endpoint explicitly; the
exporter's other standard variables (`OTEL_EXPORTER_OTLP_PROTOCOL`, headers,
timeout) still apply.

## Request id correlation

The first middleware in both pipelines handles `X-Request-Id`:

- An inbound value is accepted only when it is 1–64 characters and every
  character is `A-Z`, `a-z`, `0-9`, `.`, `_`, `:` or `-`. Anything else is
  replaced.
- A missing or rejected value becomes the current trace id (32 lowercase hex
  characters), or a GUID in `N` format when no activity exists.
- The chosen value becomes `HttpContext.TraceIdentifier`, is added to the
  current span as `request.id`, and is written to the response header before
  the rest of the pipeline runs. It is re-asserted in `OnStarting`, so a
  response reset by the exception handler or status-code pages still carries it.
- A log scope with key `request_id` wraps the rest of the request. With
  `LOG_FORMAT=json`, that scope appears on every line logged during the
  request alongside `TraceId` and `SpanId`.

This makes the 401, 404, 413, 429 and 500 envelopes traceable too. The header is
a correlation aid, not an authentication or authorization signal: a caller can
choose its own value, so do not use it for security decisions.

## Running locally with a collector

The demo ships no collector and `docker-compose.yml` does not pass these
variables into `api` or `auth`, so telemetry stays off unless you opt in. One
way to try it is a local Jaeger container plus a local Compose override:

```powershell
# Terminal 1: an OTLP receiver with a UI on http://localhost:16686
docker run --rm -p 4317:4317 -p 4318:4318 -p 16686:16686 jaegertracing/all-in-one:latest
```

Create a local `docker-compose.override.yml` (not committed) that adds the
endpoint to both services:

```yaml
services:
  api:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://host.docker.internal:4317
      LOG_FORMAT: json
  auth:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://host.docker.internal:4317
      LOG_FORMAT: json
```

Then start the stack as usual and inspect a request:

```powershell
docker compose up -d
Invoke-RestMethod http://localhost:8082/healthz -Headers @{ 'X-Request-Id' = 'demo-check-1' }
# Add ?service=sme-demo-api to the Jaeger search, or open the trace by request id.
```

`host.docker.internal` is the Docker Desktop host alias. On Linux use the host
gateway address or put the collector on the Compose network in the same local
override file. For an in-process check without a collector, set
`OTEL_CONSOLE_EXPORT=true` and read the exporter output in the service log.

## What is deliberately not done

- **No collector in Compose.** `docker-compose.yml` is unchanged so a normal
  demo run has no telemetry dependency and no extra container. A deployment
  adds its own collector and passes the endpoint in.
- **No sampling configuration.** The OpenTelemetry SDK default sampler is used,
  so every request is recorded. A production deployment should set a sampler
  and a collector-side policy.
- **No PII or SQL text.** Request bodies, authorization headers, phone numbers
  and OTPs are never added to spans or log scopes. EF Core query text is removed
  in the instrumentation callback (`db.statement`, `db.query.text` and the
  stored-procedure name), and query parameters are off. The EF span still
  records timing, provider and outcome.
- **No log exporter.** Logs stay on the console (simple or JSON); only traces
  and metrics go to OTLP. That keeps the demo's existing log workflow and
  avoids shipping a second logging pipeline.
- **No Azure SDK metrics or logs.** Only the two Azure activity sources are
  subscribed for tracing; the SDKs' own metrics and logs are not exported.

## Demo limitation

Telemetry is per instance. Each process owns its in-memory metrics and its own
exporter connection; there is no aggregation across replicas, no shared
sampling decision and no collector in the stack that survives a container
restart. A collector gives you a place to aggregate, but the demo still runs a
single `api` and a single `auth` container.
