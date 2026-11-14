# Operational Runbook

How to diagnose and recover the Compose demo and the Azure Container Apps
deployment. The build-and-run workflow is in [`development.md`](development.md);
this file is the incident and operations view. Telemetry is described in
[`observability.md`](observability.md), the release gates in
[`azure-pipelines.md`](azure-pipelines.md), and the accepted decisions in
[ADR-017](reference/adr/ADR-017-per-instance-rate-limiting-and-readiness.md),
[ADR-018](reference/adr/ADR-018-observability-and-supply-chain.md), and
[ADR-020](reference/adr/ADR-020-azure-hosting-and-test-deployment.md).

## 1. Purpose and scope

The demo runs in one of two forms. Locally it is one Compose project named
`sme-demo` on one machine: nginx and the React SPA on 8080, `auth` on 8081,
`api` on 8082, `sms-mock` on 8083, `issuer-mock` on 8084, and SQL Server,
Azurite, two one-shot migrators, and `db-grants` on the internal `sme` network.
Published ports bind to `127.0.0.1` only. The Azure form is described in §9: the
same images with Container Apps platform probes, Azure SQL, Azure Storage, and
Key Vault. The Compose playbooks in this document still apply to local work.

What this demo is:

- a runnable reference for the runtime controls in the baseline: readiness,
  rate limits, request correlation, opt-in telemetry, pinned images, and a
  hardened Compose runtime;
- disposable by design: SQL Server, Azurite, and the auth signing keys all live
  on named volumes that can be removed and recreated (Azure uses managed
  services and the `auth-keys` file share instead);
- offline in Compose: no registry, IdP, SMS gateway, or telemetry collector is
  required after the images are built.

What this demo is not:

- highly available: there is one replica of each service, one Compose host, and
  one Container Apps replica per service;
- durable: there is no backup service and no restore procedure for SQL Server or
  Azure SQL;
- internet-facing without preparation: the Compose stack is loopback-only; the
  Azure Test environment exposes the web origin over HTTPS, but still with the
  published demo credentials and demo mode. There is no WAF, no custom domain,
  and no real identity provider;
- a complete observability stack: there is no collector, no sampling policy,
  and no central log store beyond the Container Apps environment's Log
  Analytics workspace;
- a substitute for a production incident-response plan with contacts, on-call
  escalation, and audit. The demo exceptions are registered in
  [`threat-model.md`](threat-model.md) §7; every one of them must be replaced
  before an internet-facing production deployment.

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

The check runs as the in-image `SmeDemo.HealthProbe` via `dotnet`, because the
.NET runtime images have no shell or `curl`. Inspect the recorded result, exit
code, and probe stderr with
`docker inspect --format '{{json .State.Health}}' <container>`. The probe's
output is in the health log, not in `docker compose logs`, which shows only the
application's own logging.

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

**Diagnosis.** The nginx server block is not baked into the image: it is
rendered at container start by `/docker-entrypoint.d/30-upstream-config.sh` into
`/tmp/conf.d/default.conf`, from `UPSTREAM_AUTH` (default `http://auth:8081`),
`UPSTREAM_API` (default `http://api:8082`), and `NGINX_RESOLVER` (default
`127.0.0.11`). The committed template is
[`web/nginx/default.conf.template`](../web/nginx/default.conf.template). The
proxy uses a variable `proxy_pass` plus the resolver, so nginx re-resolves the
service name instead of holding the old container IP. A short 502 is still
possible while the name re-resolves or the upstream is starting. Confirm the
rendered configuration and the upstreams:

```powershell
docker compose exec web sh -c 'cat /tmp/conf.d/default.conf'
docker compose exec web sh -c 'echo $UPSTREAM_AUTH $UPSTREAM_API $NGINX_RESOLVER'
docker compose ps auth api
docker compose logs --tail 100 web
Invoke-RestMethod http://localhost:8081/healthz
Invoke-RestMethod http://localhost:8082/healthz
```

`30-upstream-config.sh` validates each value before rendering. An absolute
`http(s)` URL is required for the two upstreams, and the resolver must be a bare
IPv4 address. A rejected value prints
`30-upstream-config.sh: ERROR: ...` and exits non-zero, so nginx never starts; a
missing render produces nginx's own `[emerg] ... open() "/tmp/conf.d/default.conf"
failed` instead. Editing files under `/etc/nginx/conf.d` no longer does anything:
that directory is not included, the image ships no server block there, and the
rendered file lives on `/tmp`. Change the environment variables or the template,
then recreate `web`.

**Action.** Wait for `auth` and `api` to become healthy, then retry. If the 502
persists while both are healthy, restart nginx:

```powershell
docker compose restart web
```

Do not replace the variable `proxy_pass` with a literal upstream: that
reintroduces the stale-IP failure the resolver exists to prevent. If direct
calls to 8081/8082 work but 8080 does not, the fault is in nginx or the `sme`
network, not in the application. In the Azure deployment the same renderer runs
with the internal Container Apps FQDNs and `NGINX_RESOLVER=168.63.129.16`; see
§9.

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

In the Azure deployment the same key material lives on the `auth-keys` Azure
Files share, mounted read-write at `/keys` on the `auth` Container App. The
share, not the container filesystem, is the durable copy: a new revision keeps
the same keys, and rolling `auth` back to the previous revision does not
invalidate tokens. To rotate, remove the key files from the share and restart
the auth revision; every issued token then fails validation once `api` refreshes
its metadata cache. Deleting the share is a key-destroying action, and the share
is the only backup unit until a managed KMS/HSM replaces it (ADR-020). Treat the
share as a secret-bearing resource: it is reachable only through the platform
mount today, but it has no separate backup or access policy of its own.

## 6. Rollback and restore

| Action | How | What it affects |
|--------|-----|-----------------|
| Redeploy the previous image | Point `image:` at the previous immutable tag and `docker compose up -d --no-build`, or check out the previous commit and `docker compose up -d --build` | Application binaries only; volumes persist |
| Clean demo reset | `docker compose down -v` then `docker compose up -d --build` | Destroys `mssql-data`, `auth-keys`, and `azurite-data` |
| Restore SQL data | Not implemented in the demo | Out of scope; the volume is the only copy |

CI and Azure Pipelines build each image once. The GitHub workflow builds and
scans local images tagged with the commit SHA (`${GITHUB_SHA}`) but never
pushes. Azure Pipelines tags the pushed image with `$(Build.SourceVersion)`,
resolves the tag to an immutable digest, and promotes that digest between
environments without rebuilding. In Compose the demo builds local
`sme-demo-*:local` images, so a rollback means rebuilding from the previous
commit or retagging a previously built image. In Azure, roll back the
application with [`tools/deploy/rollback.sh`](../tools/deploy/rollback.sh); see
§9.

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
| Demo user password and OTP | `.env` (created from `.env.example`), or the Compose defaults; in Azure, the pipeline demo variables | Published by design; never reuse outside a loopback demo, and set `demoMode=false` for a real one |
| SQL Server administrator password | Compose `.env` (`MSSQL_SA_PASSWORD`); Azure Key Vault secret `sql-admin-password`, supplied to the pipeline as the `sqlAdminPassword` variable-group secret | Used by the migrators and `db-grants` only; the Azure application logins cannot run DDL |
| Application DB passwords | Compose `.env` (`SME_API_DB_PASSWORD`, `SME_AUTH_DB_PASSWORD`); Azure Key Vault `api-db-password` and `auth-db-password`, supplied as `SME_API_DB_PASSWORD` / `SME_AUTH_DB_PASSWORD` | `db-grants` (Compose) and the bootstrap pass of the migrator jobs (Azure) create the read/write-only logins with them |
| Issuer mock API key | Compose `.env` or the Compose default; Azure Key Vault `issuer-api-key`, supplied as `ISSUER_API_KEY` | Demo credential shared by `api` and `issuer-mock` |
| Auth signing key | Compose `auth-keys` volume; Azure `auth-keys` Azure Files share at `/keys` | Generated at start-up; never in the repository or image |
| Azurite account key | `docker-compose.yml` | The public emulator key; Compose only |
| Storage connection string | Azure Key Vault `storage-connection-string` | Shared-key authentication; the managed-identity migration is a follow-up |
| CI/CD credentials | GitHub Actions secrets, Azure DevOps variable groups or Key Vault | See [`azure-pipelines.md`](azure-pipelines.md) |

`.env` is gitignored and must not be committed. Images contain no secrets;
gitleaks scans the full git history in CI (full checkout), and a local scan
covers history too unless the clone is shallow.

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

## 9. Azure Container Apps deployment

The Azure line is implemented in [`infra/main.bicep`](../infra/main.bicep) and
[`tools/deploy/`](../tools/deploy/), and the pipeline stages call the same
scripts. It has **not yet run against a real Azure subscription**; ADR-020's
[Verification status](reference/adr/ADR-020-azure-hosting-and-test-deployment.md#verification-status)
is the list to prove on the first Test deployment. The Compose procedures in
§§2–8 still apply to local work; this section is the operator view of the Azure
environment.

### Deploying by hand

[`tools/deploy/deploy.sh`](../tools/deploy/deploy.sh) is driven by environment
variables; the header of the script is the authoritative contract. The required
set is `ENVIRONMENT_NAME` (`test`, `uat`, or `prod`), `RESOURCE_GROUP`,
`LOCATION`, `ACR_NAME`, `IMAGES_JSON`, `SQL_ADMIN_LOGIN`, `SQL_ADMIN_PASSWORD`,
`SME_API_DB_PASSWORD`, `SME_AUTH_DB_PASSWORD`, `ORG_ID`, `DEMO_MODE`,
`DEMO_USERNAME`, `DEMO_PASSWORD`, `DEMO_OTP`, and `ISSUER_API_KEY`. Optional:
`NAME_PREFIX` (default `smedemo`), `DEPLOYMENT_NAME`, `SOURCE_COMMIT`,
`BOOTSTRAP_SQL_PATH`, `DEPLOYMENT_RECORD_FILE`, and `SKIP_SMOKE`.

`IMAGES_JSON` is usually the output of
[`tools/deploy/resolve-digests.sh`](../tools/deploy/resolve-digests.sh) from the
Build stage, or an inline copy of that document.

```bash
export ENVIRONMENT_NAME=test
export RESOURCE_GROUP=rg-sme-demo-test
export LOCATION=australiaeast
export ACR_NAME=acrsmedemotest
export IMAGES_JSON=./image-digests/images.json
export SOURCE_COMMIT="$(git rev-parse HEAD)"
export DEPLOYMENT_RECORD_FILE=./deployment-record-test.json
# Supply these from the operator secret store / variable group, never from
# shell history: SQL_ADMIN_LOGIN, SQL_ADMIN_PASSWORD, SME_API_DB_PASSWORD,
# SME_AUTH_DB_PASSWORD, ISSUER_API_KEY, ORG_ID, DEMO_MODE, DEMO_USERNAME,
# DEMO_PASSWORD, DEMO_OTP.
bash tools/deploy/deploy.sh
```

The run requires a logged-in `az` session and a registry in the target resource
group; `deploy.sh` refuses otherwise. It applies the template, runs
`job-db-grants`, `job-migrate-api`, and `job-migrate-auth` in that order,
restarts the apps' latest revisions now that the schema exists, waits for the
five apps' latest revisions to be ready and `Running`, writes the deployment
record, and then runs the public smoke test.

The three database passwords must not contain a quote, semicolon, backslash,
backtick, or whitespace: they are substituted into a T-SQL literal and a
SqlClient connection string, and `deploy.sh` rejects a value that would corrupt
either.

### Inspecting an environment

```bash
# App and revision state
az containerapp show --name smedemo-test-api --resource-group rg-sme-demo-test \
  --query '{latest:properties.latestRevisionName,ready:properties.latestReadyRevisionName,running:properties.runningStatus}'
az containerapp revision list --name smedemo-test-api \
  --resource-group rg-sme-demo-test -o table

# Application logs (JSON: auth and api set LOG_FORMAT=json)
az containerapp logs show --name smedemo-test-web \
  --resource-group rg-sme-demo-test --tail 100
az containerapp logs show --name smedemo-test-api \
  --resource-group rg-sme-demo-test --tail 100 --follow

# One-shot jobs and their executions
az containerapp job list --resource-group rg-sme-demo-test -o table
az containerapp job execution list --name smedemo-test-job-migrate-api \
  --resource-group rg-sme-demo-test -o table
az containerapp job logs show --name smedemo-test-job-migrate-api \
  --resource-group rg-sme-demo-test --execution <execution-id> --container migrator
```

The public entry point is the `webUrl` output of the deployment (also recorded
in the deployment record). `auth`, `api`, and both mocks use internal ingress,
so they are not reachable from a workstation; use the app logs, the revision
state, and the web origin. The platform probes are the readiness authority:
`api` readiness is `GET /readyz`, `auth` and the mocks use `GET /healthz`, and
`web` uses `GET /`. The Container Apps environment sends container logs to the
Log Analytics workspace created by the template.

### Rolling back

```bash
tools/deploy/rollback.sh --environment test --resource-group rg-sme-demo-test
```

This lists revisions by creation time, activates the revision before the
newest, and moves 100% of ingress traffic to it; `--app web` (repeatable)
limits the work. It reverts the application only. Migrations are forward-only
(ADR-010, recorded in the [ADR index](reference/adr/README.md)): the database
stays on the newer schema, so the previous revision must still tolerate it.
Auth signing keys stay on the `auth-keys` file share, so an auth rollback does
not invalidate tokens. A rollback also does not undo a Bicep deployment that
changed infrastructure; redeploy the previous template state if that is the
cause.

### The auth signing-key share

`auth` reads and writes its RS256 keys at `/keys`, now backed by the
`auth-keys` Azure Files share and mounted read-write on the auth app only. A new
revision therefore keeps the same key set, and rolling auth back does not
invalidate issued tokens. The open risk is ownership: the auth image runs as a
non-root user, and Azure Files presents files as `root:root` unless the mount is
configured otherwise. If the first deployment cannot write there, the
documented fallback is to keep a single auth replica with no mount and accept
that tokens reset whenever a revision starts; record that decision and open a
follow-up. Deleting the share destroys the key material and invalidates every
token.

### First deployment checklist

From ADR-020 §Verification status, in order:

1. Bicep deploys cleanly to an empty resource group, including the Key Vault
   secret references and both role assignments.
2. The three migrator jobs exit 0, the application logins can write their own
   database, and a second run of the jobs is a no-op.
3. `api`, `auth`, `web`, and both mocks become healthy, and the platform
   readiness probe on `/readyz` passes.
4. The Azure Files mount at `/keys` is writable by the non-root auth user, or
   the fallback above is applied and recorded.
5. `environment-smoke.sh` passes against the public web origin, and a rollback
   to the previous revision works.
6. The registry-side Trivy scan of the promoted digests passes in the same
   pipeline run.

Record the evidence (deployment record, smoke output, revision names, digests)
before UAT is used.

## 10. Limits of the demo

| Limit | Demo behaviour | A production deployment must |
|-------|----------------|------------------------------|
| Rate limiting | In-memory, per process, per instance; a restart resets every window | Use a shared limiter or size budgets for the replica count, with central quotas and alerting |
| Telemetry | In-memory, per process; no collector, no sampling, no aggregation | Add a collector, a sampling policy, retention, and per-environment resource attributes |
| Availability | One replica of each service; one Compose host and one Container Apps replica per app | Multiple replicas, health-gated rollout, and an ingress that removes unhealthy instances |
| State | In-memory challenges, readiness cache, and rate-limit windows | Shared caches/stores with TTL and a documented failure mode |
| Identity | Demo issuer, fixed OTP, published password | Real IdP, MFA, revocation/refresh, and no demo mode |
| Keys | Compose `auth-keys` volume or the Azure Files share, manual rotation | KMS/HSM, audited rotation with overlap, and revocation |
| Data | Compose named volumes; Azure SQL with no backup/restore exercise | Managed database with automated backups, PITR, and tested restores |
| Transport | Compose: plain HTTP on loopback. Azure: HTTPS at the platform ingress, no WAF or custom domain | TLS at the edge, HSTS, WAF, and no direct host ports |
| Logs | Console only in Compose; Container Apps log analytics in Azure | Central log store with retention, alerts, and an audit trail |

The same limitations are recorded as decisions in
[ADR-017](reference/adr/ADR-017-per-instance-rate-limiting-and-readiness.md),
[ADR-018](reference/adr/ADR-018-observability-and-supply-chain.md), and
[ADR-020](reference/adr/ADR-020-azure-hosting-and-test-deployment.md). If an
incident needs more than the steps in this runbook, the demo is being used as a
production system; fix the deployment gap before continuing.
