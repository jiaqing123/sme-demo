# Operational Runbook

How to diagnose and recover the Compose demo, and what a real deployment has to
replace. The build-and-run workflow is in [`development.md`](development.md); this
file is the incident and operations view. Telemetry is described in
[`observability.md`](observability.md), the release gates in
[`azure-pipelines.md`](azure-pipelines.md), and the accepted decisions in
[ADR-017](reference/adr/ADR-017-per-instance-rate-limiting-and-readiness.md) and
[ADR-018](reference/adr/ADR-018-observability-and-supply-chain.md).

## 1. Purpose and scope

The demo is one Compose project named `sme-demo` on one machine: nginx and the
React SPA on 8080, `auth` on 8081, `api` on 8082, `sms-mock` on 8083,
`issuer-mock` on 8084, and SQL Server, Azurite, two one-shot migrators, and
`db-grants` on the internal `sme` network. Published ports bind to `127.0.0.1`
only.

What this demo is:

- a runnable reference for the runtime controls in the baseline: readiness,
  rate limits, request correlation, opt-in telemetry, pinned images, and a
  hardened Compose runtime;
- disposable by design: SQL Server, Azurite, and the auth signing keys all live
  on named volumes that can be removed and recreated;
- offline: no registry, IdP, SMS gateway, or telemetry collector is required
  after the images are built.

What this demo is not:

- highly available: there is one replica of each service and one host;
- durable: there is no backup service and no restore procedure for SQL Server;
- internet-facing: TLS, a real identity provider, a real SMS gateway, and a
  managed secret store are all out of scope;
- a complete observability stack: there is no collector, no sampling policy,
  and no central log store;
- a substitute for a production incident-response plan with contacts, on-call
  escalation, and audit. The demo exceptions are registered in
  [`threat-model.md`](threat-model.md) §7; every one of them must be replaced
  before an internet-facing deployment.

## 2. Health model

Both services expose liveness. Only `api` exposes dependency readiness.

| Probe | Service | Kind | Success | Failure |
|-------|---------|------|---------|---------|
| `GET /healthz` | `api` (8082), `auth` (8081) | liveness | `200 {"status":"ok"}` | the process is not answering or has exited |
| `GET /readyz` | `api` (8082) | readiness | `200 {"status":"ready","checks":[...]}` | `503 {"status":"degraded","checks":[...]}` with `Retry-After: 5` |

`/readyz` checks three dependencies in parallel under one 5 second budget:

| Check name | Proves |
|------------|--------|
| `database` | SQL Server `sme_api` is reachable with the application login |
| `blob-storage` | Azurite Blob Storage is reachable |
| `auth-metadata` | The auth OIDC discovery document is reachable, names the configured `JWT_ISSUER`, and has an absolute `http(s)` `jwks_uri` |

Each entry carries only `name` and `status`; exception messages, connection
strings, and HTTP response bodies never appear in the readiness response. A
probe that throws or overruns the deadline is reported as `degraded`.

Readiness snapshots are cached for 2 seconds and refreshes are single-flight:
concurrent probes share one in-flight check instead of stampeding SQL Server,
Blob Storage, and auth. See
[`ReadinessService.cs`](../src/SmeDemo.Api/Readiness/ReadinessService.cs).

Because Compose's `api` healthcheck calls `/readyz` while `auth` uses `/healthz`:

- `api` shows `unhealthy` in `docker compose ps` when a dependency is down, even
  though the process is alive and `/healthz` still returns `200`;
- `auth` shows `unhealthy` only when the auth process itself stops answering.

### Inspect health

```powershell
docker compose ps
docker compose ps -a     # includes the one-shot migrators and db-grants

Invoke-RestMethod http://localhost:8081/healthz
Invoke-RestMethod http://localhost:8082/healthz

# Invoke-RestMethod throws on 503; use -SkipHttpErrorCheck to read the body.
$ready = Invoke-WebRequest -SkipHttpErrorCheck http://localhost:8082/readyz
$ready.StatusCode
$ready.Content
$ready.Headers['Retry-After']
```

A healthy stack:

```json
{"status":"ready","checks":[
  {"name":"database","status":"ready"},
  {"name":"blob-storage","status":"ready"},
  {"name":"auth-metadata","status":"ready"}
]}
```

The one-shot containers showing `Exited (0)` in `docker compose ps -a` is the
expected end state for `db-migrator`, `db-migrator-auth`, and `db-grants`.

## 3. First 5 minutes of an incident

1. **Scope the failure.** `docker compose ps` shows which containers are
   `healthy`, `unhealthy`, `restarting`, or absent. Use `ps -a` to see the
   one-shot services.
2. **Test liveness.** `GET /healthz` on 8081 and 8082. A `200` means the process
   is up and the problem is a dependency, a credential, or a request; a
   connection failure means the container or port is down.
3. **Read readiness.** `GET /readyz` and inspect the `checks` array. The failing
   check names the first dependency to investigate. `Retry-After: 5` is a hint,
   not a promise that recovery takes 5 seconds.
4. **Read the last 100 log lines.** API and auth logs are JSON in Compose
   because both services set `LOG_FORMAT=json`:

   ```powershell
   docker compose logs --tail 100 api auth
   docker compose logs --tail 100 web mssql azurite
   ```

5. **Correlate by request id.** Every response carries `X-Request-Id`. The same
   value appears as `request_id` in the request's log scope, next to `TraceId`
   and `SpanId`:

   ```powershell
   $response = Invoke-WebRequest -SkipHttpErrorCheck http://localhost:8082/readyz
   $requestId = $response.Headers['X-Request-Id']
   $requestId
   docker compose logs api | Select-String -SimpleMatch $requestId
   ```

   In the browser, read the header from the failing response in the network
   panel. The id is a correlation aid only; a caller can choose its own value, so
   never use it for authorization.

6. **Change one thing.** Restarting a container resets its in-memory rate
   limits, readiness cache, and OTLP exporter connection. It also discards
   nothing on a volume. Prefer recovering the named dependency over restarting
   the caller, and never start with `docker compose down -v`.

## 4. Common failure playbooks

### 4.1 `api` is unhealthy but `/healthz` returns 200

**Symptom.** `docker compose ps` shows `api` as `unhealthy`; `/healthz` on 8082
returns `200 {"status":"ok"}`.

**Diagnosis.** The Compose healthcheck uses `/readyz`, so this is a dependency
failure, not a dead process. Read the `checks` array:

```powershell
Invoke-WebRequest -SkipHttpErrorCheck http://localhost:8082/readyz |
  Select-Object -ExpandProperty Content
docker compose ps mssql azurite auth
docker compose logs --tail 100 api auth mssql azurite
```

| Failing check | First thing to check |
|---------------|----------------------|
| `database` | `mssql` health; `db-grants` exit status; the application login and `.env` passwords |
| `blob-storage` | `azurite` health; the `AZURE_STORAGE_CONNECTION_STRING` endpoints |
| `auth-metadata` | `auth` health; `AUTH_METADATA_URL`; `AUTH_REQUIRE_HTTPS_METADATA=false` in Compose |

**Action.** Recover the dependency. Do not restart `api` to clear a dependency
failure; the readiness snapshot refreshes within 2 seconds of the dependency
returning. Restart `api` only when you have changed a setting it reads at
start-up, or when the metadata cache itself is the problem (see 4.3). A short
readiness flap can still be visible during an Azurite or SQL Server restart;
that is expected.

### 4.2 `auth` returns 503 on login

**Symptom.** `POST /auth/login` (and resend) returns `503` with
`UPSTREAM_UNAVAILABLE` and a readable message such as "The SMS provider is
unavailable. Try again shortly." `auth` itself stays `healthy`.

**Diagnosis.** `auth` posts every code to `SMS_PROVIDER_URL` and does not treat
a missing SMS provider as a reason to stop. Check the provider and the log:

```powershell
docker compose ps sms-mock
docker compose logs --tail 100 auth sms-mock
```

**Action.** Start `sms-mock` (`docker compose start sms-mock` or
`docker compose up -d sms-mock`) or correct `SMS_PROVIDER_URL`, then retry.

- A failed login does not create a challenge, so the caller can retry
  immediately.
- A failed resend keeps the already-updated challenge, so the next attempt has
  to wait for the 60 second resend cooldown.
- In demo mode the fixed `123456` is still posted to the mock; the log line is
  `Your SME Demo verification code is 123456`.

### 4.3 `401 INVALID_TOKEN` right after an auth restart

**Symptom.** A token that was valid a moment ago is rejected by `api` with `401`
and `INVALID_TOKEN`, often immediately after `auth` restarted or after a key
rotation.

**Diagnosis.** `api` caches the auth OIDC metadata document, including the JWKS.
After an auth restart or key change, the cached document can be stale, so the
token's `kid` is not in the cached key set. Compare the token header with the
live JWKS:

```powershell
Invoke-RestMethod http://localhost:8081/.well-known/jwks.json | ConvertTo-Json -Depth 4
docker compose logs --tail 100 api auth
```

**Action.** Wait for the metadata refresh, or restart `api` to discard the cache
immediately, then retry. If the `auth-keys` volume was deleted, the token really
is invalid: the signature key is gone, so sign in again (see section 5).

Distinguish this from a metadata outage: if every authenticated `/api/v1` call
returns `503 UPSTREAM_UNAVAILABLE` with `Retry-After: 5`, the metadata/JWKS fetch
failed at the transport layer and the caller's token was never judged. Check
`docker compose ps auth`, `AUTH_METADATA_URL`, and
`AUTH_REQUIRE_HTTPS_METADATA=false` in Compose. Signature, issuer, audience,
expiry, and algorithm failures remain genuine `401`s.

### 4.4 `429 RATE_LIMITED`

**Symptom.** Login, verify, or resend returns `429`; or `/api/v1` returns `429`.
The response carries a numeric `Retry-After`.

**Diagnosis.** A fixed-window budget is exhausted.

| Service | Budget | Default | Configuration |
|---------|--------|---------|---------------|
| `auth` | Login per IP | 20 per minute | `AUTH_RATE_LIMIT_LOGIN_IP`, `AUTH_RATE_LIMIT_LOGIN_IP_WINDOW` |
| `auth` | Login per account | 10 per 5 minutes | `AUTH_RATE_LIMIT_LOGIN_ACCOUNT`, `AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW` |
| `auth` | Verify per IP | 60 per minute | `AUTH_RATE_LIMIT_VERIFY_IP`, `AUTH_RATE_LIMIT_VERIFY_IP_WINDOW` |
| `auth` | Resend per IP | 10 per minute | `AUTH_RATE_LIMIT_RESEND_IP`, `AUTH_RATE_LIMIT_RESEND_IP_WINDOW` |
| `api` | `/api/v1`, per user and endpoint | 120 per minute | `API_RATE_LIMIT_PERMIT`, `API_RATE_LIMIT_WINDOW` |

**Action.** Wait for `Retry-After`, or raise the matching setting and recreate
the service. The windows are in memory and per process: a restart resets them,
but that is a demo convenience, not a production remedy.

- If every browser client sees `429` at the same time, suspect forwarded
  headers rather than a single abusive client. Compose sets
  `AUTH_TRUST_FORWARDED_HEADERS=true` so `auth` keys the per-IP budget on
  nginx's `X-Forwarded-For`; a proxy that stops appending the client address
  makes all callers share one bucket.
- `/healthz` and `/readyz` are outside the rate-limited path and are never
  limited.
- The per-account login key is the trimmed, lower-cased username; it is never
  looked up or reflected in the 429.

### 4.5 Database migration failed

**Symptom.** `db-migrator` or `db-migrator-auth` exits non-zero. `db-grants`,
`auth`, `api`, and `web` are "not started" or never become healthy.

**Diagnosis.** A one-shot migrator applies the committed, idempotent SQL and
exits; a failure stops everything that waits on it:

```powershell
docker compose ps -a
docker compose logs --tail 100 db-migrator db-migrator-auth db-grants mssql
```

The migrators wait for SQL Server and for their target database, retrying for
`DB_WAIT_SECONDS` (120 in Compose), so a transient "database is still recovering"
message is normally retried rather than fatal.

**Action.** Re-run the failed step; the scripts are idempotent:

```powershell
docker compose run --rm db-migrator
docker compose run --rm db-migrator-auth
docker compose up -d db-grants
```

Common causes:

- `sa` password changed after the `mssql-data` volume was initialised. Either
  restore the original `MSSQL_SA_PASSWORD` or recreate the volume.
- The application logins did not get their passwords: `db-grants` exited
  non-zero, or `.env` changed after it ran. Re-run `db-grants`.
- The committed SQL is stale or wrong: regenerate it with the workflow in
  [`development.md`](development.md#changing-a-schema); never hand-edit an
  applied migration.

**Clean reset (destroys demo data).** Only when the data is disposable:

```powershell
docker compose down -v
docker compose up -d --build
```

`-v` removes `mssql-data`, `auth-keys`, and `azurite-data`. Do not use it on a
database you care about; it is not a migration rollback.

### 4.6 nginx returns 502 after a container recreate

**Symptom.** `http://localhost:8080/auth/...` or `/api/...` returns `502` after
`auth` or `api` was recreated, while the service itself is healthy.

**Diagnosis.** nginx uses a variable `proxy_pass` plus Docker's embedded DNS
(`resolver 127.0.0.11 valid=10s`), so it re-resolves the service name instead of
holding the old container IP. A short 502 is still possible while the name
re-resolves or the upstream is starting. Confirm the upstreams:

```powershell
docker compose ps auth api
docker compose logs --tail 100 web
Invoke-RestMethod http://localhost:8081/healthz
Invoke-RestMethod http://localhost:8082/healthz
```

**Action.** Wait for `auth` and `api` to become healthy, then retry. If the 502
persists while both are healthy, restart nginx:

```powershell
docker compose restart web
```

Do not replace the variable `proxy_pass` with a literal upstream: that
reintroduces the stale-IP failure the resolver exists to prevent. If direct
calls to 8081/8082 work but 8080 does not, the fault is in nginx or the `sme`
network, not in the application.

## 5. Key rotation

`auth` generates an RS256 signing key on first start and writes it to the
`auth-keys` volume (mounted at `/keys`). A restart keeps the same `kid`, so
existing tokens stay valid. The private key never leaves the issuer or reaches
source control.

### Rotate by deleting the key volume

This starts a fresh key set and invalidates every issued token:

```powershell
docker compose stop auth
docker volume ls | Select-String 'auth-keys'   # expect sme-demo_auth-keys
docker volume rm sme-demo_auth-keys
docker compose up -d auth
```

`docker compose down -v` also removes the volume, but it destroys the databases
and storage as well. Remove only `auth-keys` when only the key should change.

After rotation:

- previously issued tokens fail signature validation once `api` refreshes its
  metadata cache; before that refresh, the cached old JWKS can still accept an
  old token;
- a token minted with the new key can return `401 INVALID_TOKEN` until `api`
  refreshes the cache, because its `kid` is not in the cached document yet.
  Restart `api` to force the refresh, or wait for the next metadata refresh.

The store can also load a `signing-key.previous.json` from the volume when it is
present, so a staged rotation can keep old tokens verifiable during an overlap.
There is no rotation endpoint or automated overlap in the demo; treat this as a
manual demo-scale procedure. Production must use a managed key service (KMS/HSM)
with an audited rotation, overlapping key IDs, and revocation.

## 6. Rollback and restore

| Action | How | What it affects |
|--------|-----|-----------------|
| Redeploy the previous image | Point `image:` at the previous immutable tag and `docker compose up -d --no-build`, or check out the previous commit and `docker compose up -d --build` | Application binaries only; volumes persist |
| Clean demo reset | `docker compose down -v` then `docker compose up -d --build` | Destroys `mssql-data`, `auth-keys`, and `azurite-data` |
| Restore SQL data | Not implemented in the demo | Out of scope; the volume is the only copy |

CI and Azure Pipelines build each image once and tag it with the commit SHA
(`${GITHUB_SHA}` / `$(Build.SourceVersion)`); promotion moves that tag or digest
between environments and never rebuilds. The demo itself builds local
`sme-demo-*:local` images, so a rollback means rebuilding from the previous
commit or retagging a previously built image.

Two caveats:

- **The schema is forward-only.** The committed scripts are idempotent, but
  there is no down-script or rollback path for an applied migration. Rolling the
  application back while the database stays on the newer schema can fail; a real
  deployment should use expand/contract migrations and keep the application
  compatible with the previous schema during rollout.
- **Keep the key volume.** A rollback that also removes `auth-keys` invalidates
  every session. Leave the volume in place unless rotation is the intent.

**SQL restore is out of scope for the demo.** There is no backup schedule, no
retention, and no tested restore. A real deployment needs managed automated
backups with point-in-time recovery, a documented RPO/RTO, regularly tested
restores, and a backup of the signing-key material.

## 7. Security operations

### Where secrets live

| Secret | Location | Notes |
|--------|----------|-------|
| Demo user password and OTP | `.env` (created from `.env.example`), or the Compose defaults | Published by design; never reuse outside a loopback demo |
| SQL Server `sa` password | `.env` (`MSSQL_SA_PASSWORD`) | Used by the migrators and `db-grants` only |
| Application DB passwords | `.env` (`SME_API_DB_PASSWORD`, `SME_AUTH_DB_PASSWORD`) | `db-grants` creates read/write-only logins with them |
| Issuer mock API key | `.env` or the Compose default | Demo credential shared by `api` and `issuer-mock` |
| Auth signing key | `auth-keys` volume | Generated at start-up; never in the repository or image |
| Azurite account key | `docker-compose.yml` | The public emulator key; demo only |
| CI/CD credentials | GitHub Actions secrets, Azure DevOps variable groups or Key Vault | See [`azure-pipelines.md`](azure-pipelines.md) |

`.env` is gitignored and must not be committed. Images contain no secrets;
gitleaks scans the working tree in CI.

### Published credentials are demo-only

The demo user is `demo` / `Demo_Passw0rd!` with OTP `123456`; the mock issuer key
is `demo-issuer-key`, and the Azurite key is the documented emulator key. These
values are in the repository and on the sign-in hint while `DEMO_MODE` is
enabled. They are not secrets and must never be reused in a shared or
internet-facing environment.

### If the demo password leaks or an environment is exposed

1. **Stop the exposure.** The stack is loopback-only by design. Remove any
   `0.0.0.0` port mapping, do not forward the ports, and treat every credential
   in `.env.example` as compromised.
2. **Leave demo mode.** Set `DEMO_MODE=false` and change `DEMO_USERNAME`,
   `DEMO_PASSWORD`, and `DEMO_PHONE`. Clear `DEMO_OTP` and set
   `SMS_PROVIDER_URL` to a real gateway: `auth` refuses to start when demo mode
   is off and the fixed OTP is still set, the SMS URL is missing, or the
   username/password/phone still hold the published values.
3. **Rotate the database passwords.** Change `MSSQL_SA_PASSWORD`,
   `SME_API_DB_PASSWORD`, and `SME_AUTH_DB_PASSWORD`. The existing
   `mssql-data` volume still has the old `sa` password, so either recreate the
   volumes in the demo or change it with `ALTER LOGIN` using the old password
   before updating `.env`. Re-running `db-grants` updates the two application
   login passwords (the script uses `ALTER LOGIN` when a login already exists),
   but it connects as `sa`, so the `sa` password in `.env` must already match
   the database.
4. **Rotate the signing key.** Remove the `auth-keys` volume (section 5) so
   tokens minted under the old key cannot be validated.
5. **Recreate the services.** `docker compose up -d --force-recreate` after the
   `.env` edit. For a disposable demo, `docker compose down -v` followed by
   `docker compose up -d --build` gives a clean state.
6. **In production,** revoke sessions and refresh tokens through the real
   identity provider, rotate secrets through the secret manager, preserve the
   audit trail, and follow the incident process. A demo password leak is also a
   signal that the stack was exposed beyond localhost, so investigate how.

## 8. Observability

Compose sets `LOG_FORMAT=json` for `api` and `auth`, so their logs are one JSON
object per line. Scopes are included and each request line carries `request_id`,
`TraceId`, and `SpanId`. Local `dotnet run` keeps the human-readable simple
console format unless `LOG_FORMAT=json` is set.

Telemetry is registered in both hosts but exports nothing by default. Turn it on
with environment variables, preferably through a local, uncommitted Compose
override:

| Variable | Effect |
|----------|--------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Exports traces and metrics to this OTLP endpoint |
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | Exports only traces; wins over the generic endpoint for traces, and does not enable metrics |
| `OTEL_CONSOLE_EXPORT=true` | Also prints spans and metric batches to the container log; useful for a short check, noisy for a running stack |

There is no default `localhost:4317` guess: with no endpoint configured the
signals are collected and dropped. The local collector recipe, the exact
resource attributes, and the redaction rules are in
[`observability.md`](observability.md#running-locally-with-a-collector).

What to look at first:

1. **Logs by request id.** Find the `X-Request-Id` from the failing response,
   then filter the API or auth log for it. The same line has the trace and span
   ids.
2. **The trace by id.** Open the trace in the collector UI and follow the
   request through ASP.NET Core, the outgoing `HttpClient` call (auth metadata,
   issuer), and the EF Core or Azure Storage span.
3. **Dependency spans.** A slow or failed `database`, Blob/Table, or auth
   metadata span explains a readiness failure faster than the aggregate probe
   does. Health probes themselves are excluded from tracing, so `/healthz` and
   `/readyz` do not appear as spans.
4. **Metrics.** Request rate, error rate, and duration come from the ASP.NET
   Core and `HttpClient` instruments; GC, thread pool, CPU, and working set come
   from runtime metrics.

Logs stay on the console; there is no log exporter. EF Core query text and
parameters are stripped from spans, and request bodies, authorization headers,
phone numbers, and OTPs are never added as attributes.

## 9. Limits of the demo

| Limit | Demo behaviour | A production deployment must |
|-------|----------------|------------------------------|
| Rate limiting | In-memory, per process, per instance; a restart resets every window | Use a shared limiter or size budgets for the replica count, with central quotas and alerting |
| Telemetry | In-memory, per process; no collector, no sampling, no aggregation | Add a collector, a sampling policy, retention, and per-environment resource attributes |
| Availability | One replica of each service on one host | Multiple replicas, health-gated rollout, and an ingress that removes unhealthy instances |
| State | In-memory challenges, readiness cache, and rate-limit windows | Shared caches/stores with TTL and a documented failure mode |
| Identity | Demo issuer, fixed OTP, published password | Real IdP, MFA, revocation/refresh, and no demo mode |
| Keys | Local `auth-keys` volume, manual rotation | KMS/HSM, audited rotation with overlap, and revocation |
| Data | Named volumes, no backups | Managed database with automated backups, PITR, and tested restores |
| Transport | Plain HTTP on loopback | TLS at the edge, HSTS, and no direct host ports |
| Logs | Console only, JSON or simple | Central log store, retention, alerts, and an audit trail |

The same limitations are recorded as decisions in
[ADR-017](reference/adr/ADR-017-per-instance-rate-limiting-and-readiness.md) and
[ADR-018](reference/adr/ADR-018-observability-and-supply-chain.md). If an
incident needs more than the steps in this runbook, the demo is being used as a
production system; fix the deployment gap before continuing.
