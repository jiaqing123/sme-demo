# Development Guide

Practical notes for working in this repository. The design rationale lives in
[`requirements.md`](requirements.md); this file only says how to run things.

## Prerequisites

- .NET 10 SDK
- Docker Desktop with Compose v2
- PowerShell 7 or a POSIX shell

## Layout

```
SmeDemo.slnx                        solution (XML format, .NET 10 SDK)
Directory.Build.props               shared build settings
Directory.Packages.props            all package versions, pinning included
src/SmeDemo.Api                    resource API endpoints, auth wiring, composition root
src/SmeDemo.Application            command/query handlers, DTOs, validators, port interfaces
src/SmeDemo.Domain                 entities and invariants
src/SmeDemo.Infrastructure         EF Core model, migrations, Table/Blob adapters, seeders
src/SmeDemo.Auth                   independent JWT issuer (own database and keys)
web/                               React 18 + TypeScript + Vite SPA; nginx serves it in Compose
tools/SmsMock                      stand-in SMS provider that logs messages
tools/IssuerMock                   stand-in card issuer serving the deterministic fixture
tools/Migrator                     one-shot schema applier (runs in its own container)
db/migrations/api/*.sql            generated, committed, idempotent API schema scripts
db/migrations/auth/*.sql           generated, committed, idempotent auth schema scripts
db/bootstrap/app-logins.sql        creates the read/write-only application logins
openapi/v1.json                    generated OpenAPI document (committed build artefact)
tests/IssuerMock.Tests             fixture contract, auth and ordering tests
tests/SmsMock.Tests                SMS mock HTTP contract tests
tests/SmeDemo.Application.Tests    handler and validator unit tests
tests/SmeDemo.Api.Tests            integration tests through WebApplicationFactory
tests/SmeDemo.Architecture.Tests   layering, endpoint-purity, and endpoint-metadata rules
tests/SmeDemo.Auth.Tests           challenge-rule and token-claim tests
tests/SmeDemo.Infrastructure.Tests IssuerSyncService tests against fake ports/InMemory EF
docker-compose.yml                 SQL Server, migrators, grants, Azurite, sms-mock, issuer-mock, auth, API
docker-compose.dcproj              Visual Studio Compose project (F5 debugging of auth and API)
docs/                              requirements, reference baseline, licensing, native dispatch standard, workshop
```

## First run

```powershell
Copy-Item .env.example .env      # then change the passwords
docker compose up -d --build
```

Published ports are bound to `127.0.0.1`, so `localhost:8081`–`8084` work from
this machine but not from another host on the network. The demo password and
OTP are documented in this repository and therefore public; loopback binding
keeps them from becoming a login path into someone else's machine. If a remote
device has to reach the stack, override the port mappings in a local compose
override file rather than committing a `0.0.0.0` binding.

The two migrator containers run once and exit, then `db-grants` creates the
application logins. `auth` and `api` start only after the migrators and grants
succeed. Check them with:

```powershell
docker inspect --format '{{.State.Health.Status}}' sme-demo-auth-1
docker inspect --format '{{.State.Health.Status}}' sme-demo-api-1
```

Expected output for both: `healthy`.

Verify the auth schema:

```powershell
docker exec sme-demo-mssql-1 /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U sa -P "Local_Dev_Only_123!" -C `
  -d sme_auth -Q "SELECT name FROM sys.tables"
```

You should see `users` and `__EFMigrationsHistory`.

## Health and readiness

`auth` is checked with `/healthz` liveness; `api`'s Compose healthcheck calls
`/readyz`, which proves SQL Server, Blob Storage, and auth metadata are
reachable. Both endpoints are anonymous. Compose runs these checks with the
in-image `SmeDemo.HealthProbe` (`dotnet /healthprobe/SmeDemo.HealthProbe.dll`)
because the .NET runtime images have no shell or `curl`:

```powershell
Invoke-RestMethod http://localhost:8081/healthz   # {"status":"ok"}
Invoke-RestMethod http://localhost:8082/healthz   # liveness; 200 even when a dependency is down
Invoke-RestMethod http://localhost:8082/readyz    # {"status":"ready","checks":[...]}
```

`/readyz` runs its probes in parallel under a 5 second budget. A failed probe
returns `503` with `Retry-After: 5` and
`{"status":"degraded","checks":[{"name":"database","status":"degraded"},...]}`.
`Invoke-RestMethod` throws on a 503, so use
`Invoke-WebRequest -SkipHttpErrorCheck` (PowerShell 7) when you want to inspect
the degraded body.
`/healthz` stays `200`, which is why a dependency outage makes `api` show
unhealthy in `docker compose ps` without restarting the process. `GET /readyz`
is also part of the OpenAPI document.

## Visual Studio and Docker Compose

`SmeDemo.slnx` contains `docker-compose.dcproj`, the Visual Studio orchestration
project for `docker-compose.yml`. Open the solution, set `docker-compose` as the
startup project and choose a launch profile:

| Profile | What starts |
|---------|-------------|
| SME Demo (compose) | every service; breakpoints work in `SmeDemo.Api` and `SmeDemo.Auth` |
| SME Demo (API only) | the dependencies and the API, without `web` |

Create `.env` first, as in [First run](#first-run). F5 runs the Debug
configuration in Fast mode: Visual Studio builds only each Dockerfile's `base`
stage, mounts the project directory over `/app` and launches the locally built
assembly there. That is why the API and auth Dockerfiles keep a separate runtime
stage named `base`. The debuggee assembly paths and the `base` build target come
from `docker-compose.vs.debug.yml`, which Visual Studio merges only for debugging
and `docker compose up` ignores. If a project's target framework changes, update
the two `bin/Debug/net…` paths in that file.

Visual Studio and the CLI share the `sme-demo` Compose project name and the same
volumes, so stop the CLI stack before pressing F5:

```powershell
docker compose down
```

The `db-migrator`, `db-migrator-auth`, `db-grants`, `azurite`, `sms-mock` and
`issuer-mock` services are not debugged. In particular, the one-shot migrators
must not get Visual Studio's watch-and-wait entrypoint, or they would never
complete and `db-grants` would wait forever. They are listed in
`DockerComposeProjectsToIgnore` and run from the normal Compose definition.

### Breakpoints in `tools/*` do not stop

That is the configuration, not a broken debugger. `serviceActions` in
`launchSettings.json` starts `sms-mock`, `issuer-mock`, `db-migrator`,
`db-migrator-auth` and `db-grants` with `StartWithoutDebugging`, and the three
.NET ones are also in `DockerComposeProjectsToIgnore`, so Visual Studio never
attaches to them and no breakpoint in their `Program.cs` can be hit. Only
`SmeDemo.Api` and `SmeDemo.Auth` are debugged by F5.

To step through `sms-mock` or `issuer-mock`, attach the debugger to the running
container instead: **Debug > Attach to Process**, *Connection type* = **Docker
(Linux Container)**, pick the container, then the `dotnet` process, and attach
as **Managed (.NET Core for Unix)**. The container keeps running, so breakpoints
hit on the next request.

`db-migrator` and `db-grants` are not debugging targets: they are one-shot
processes that exit in about a second, and the SQL they apply is generated by
`dotnet ef` rather than written by hand, so a schema change goes through
[migrations and `docker compose run`](#changing-a-schema). If you ever do need
to run the migrator from the host, pass `DATABASE_URL` and `MIGRATIONS_PATH` to
`dotnet run --project tools/Migrator`, and use `127.0.0.1,1433` rather than
`localhost,1433` — `localhost` resolves to `::1` first, while a published Docker
port listens on IPv4 loopback only.

The `db-migrator-1`, `db-migrator-auth-1` and `db-grants-1` containers showing
`Exited (0)` in `docker compose ps -a` is the expected end state for those
one-shot services.

### The F5 popup (0x80070057)

Visual Studio reports a failed Compose launch as a generic debugger error,
typically `Unable to start debugging. Operation not supported. Unknown error:
0x80070057.`, and its output pane is not always populated. The same popup covers
two very different situations; the containers tell you which one you have:

**Nothing starts.** Then the launch really failed. Run the stack from the CLI to
see the message Visual Studio hides:

```powershell
docker compose up -d
```

A one-shot migrator exiting non-zero is the usual cause. Compose then reports
`service "db-migrator" didn't complete successfully: exit 1`, and every service
that waits on it — `db-grants`, `api`, `auth`, `web` — is "not started", so
Visual Studio has nothing to attach to. `docker compose logs db-migrator` shows
the reason. The migrator already waits for SQL Server *and* for its target
database to come online, so the transient `Cannot open database "sme_api"
requested by the login. The login failed.` that appears while SQL Server is
still recovering a database is retried instead of failing the run.

`dependency failed to start: container sme-demo-azurite-1 is unhealthy` is the
other variant: a healthcheck window that was too tight for the machine.
Compose aborts every pending container the same way. The windows in
`docker-compose.yml` are sized for a cold Docker Desktop; on a slower machine
raise the offending service's `start_period` and `retries`. Re-running
`docker compose up -d` converges the stack, and the migrators are idempotent.

**Everything starts and debugging works.** Then the popup is a Visual Studio
defect, not a repository one, and it is safe to dismiss: the failing call is
fire-and-forget, so the debug session proceeds without it. Confirm with
`docker compose ps` (the services are healthy) and by loading the app at
<http://localhost:8080>. The activity log names the failure precisely — a
`COMException (0x89710015)` raised inside the Compose project system's launch
provider:

```
System.Runtime.InteropServices.COMException (0x89710015): Unable to start debugging.
Operation not supported. Unknown error: 0x80070057.
   at Microsoft.VisualStudio.Shell.Interop.IVsDebugger4.LaunchDebugTargets4(...)
   at Microsoft.VisualStudio.Docker.Compose.ProjectSystem.DebugLaunchProvider.<LaunchContainerAsync>...
```

and, in the same launch, a failed activation of the debugger's Edit and Continue
service:

```
ServiceActivationFailedException: Activating the
"Microsoft.VisualStudio.Debugger.ManagedEditAndContinueVsdbgEngineService (0.1)"
service failed. ---> System.InvalidCastException: Unable to cast object of type
'...EditAndContinueVsdbgEngineServiceFactory' to type '...IManagedEditAndContinueEngineService'.
```

To capture that yourself, start Visual Studio with `/Log` — the switch takes an
optional file name, and without one the log is written on exit to
`%APPDATA%\Microsoft\VisualStudio\18.0_<instance>\ActivityLog.xml`:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" `
  "D:\GitHub\sme-demo\SmeDemo.slnx" /Log
```

Observed on 18.11.12328.437. Deleting `%USERPROFILE%\vsdbg` only makes Visual
Studio reinstall the same 18.10.10709.3 build and changes nothing, so the cached
debugger bits are not the cause; 18.11 has no standalone "Enable Edit and
Continue" option to switch the failing service off either. A repair or update of
the Visual Studio installation is the remaining lever.

Do not try to silence the popup by removing `DependencyAwareStart` from
`docker-compose.dcproj`. Fast mode gives each debuggee a
`DistrolessHelper --wait` entrypoint that launches the app only after the
debugger attaches, so a single blocking `compose up` then waits for a health
check that can never pass: `auth` stays `unhealthy`, `api` and `web` are "not
started", and Compose fails with `DT1001`. The property is what makes Fast-mode
debugging start services one at a time and attach in between.

## Auth service

`auth` is an independent service on port 8081. It owns the `sme_auth` database
and the `auth-keys` volume; it has no project reference to the API layers.

### Demo credentials

| Value | Default |
|-------|---------|
| Username | `demo` (`DEMO_USERNAME`) |
| Password | `Demo_Passw0rd!` (`DEMO_PASSWORD`, or `.env`) |
| Phone | `+61400000000` (`DEMO_PHONE`) |
| OTP | `123456` (`DEMO_OTP`) |
| Issuer | `http://auth:8081` (`JWT_ISSUER`) |
| Audience | `sme-demo-api` (`JWT_AUDIENCE`) |

The single user is seeded at start-up. On later starts the demo account follows
the environment values (username, password, phone, org), so changing
`DEMO_PASSWORD` in `.env` and restarting `auth` takes effect without wiping the
volume. With
`DEMO_MODE=false` the service refuses to start if `DEMO_OTP` is set or if
`SMS_PROVIDER_URL` is missing.

### Auth rate limits

`auth` applies fixed-window budgets to the abuse-prone endpoints. The per-account
login budget is checked inside the login flow, after request-shape validation,
so a malformed body cannot drain another account's budget.

| Budget | Default | Configuration |
|--------|---------|---------------|
| Login per IP | 20 per minute | `AUTH_RATE_LIMIT_LOGIN_IP`, `AUTH_RATE_LIMIT_LOGIN_IP_WINDOW` |
| Login per account | 10 per 5 minutes | `AUTH_RATE_LIMIT_LOGIN_ACCOUNT`, `AUTH_RATE_LIMIT_LOGIN_ACCOUNT_WINDOW` |
| Verify per IP | 60 per minute | `AUTH_RATE_LIMIT_VERIFY_IP`, `AUTH_RATE_LIMIT_VERIFY_IP_WINDOW` |
| Resend per IP | 10 per minute | `AUTH_RATE_LIMIT_RESEND_IP`, `AUTH_RATE_LIMIT_RESEND_IP_WINDOW` |

The account key is the trimmed username lower-cased; it is never looked up or
reflected in the 429, so the response does not reveal whether the account exists.
Every budget is in-memory and per process: restarting `auth` resets all windows,
and replicas do not share one. Exhaustion returns `429 RATE_LIMITED` with a
numeric `Retry-After`; the discovery, JWKS, userinfo, and `/healthz` endpoints
are not limited. Windows accept the same duration forms as the other settings
(`1m`, `30s`).

### Smoke test

```powershell
$base = 'http://localhost:8081'
$login = Invoke-RestMethod -Method Post -Uri "$base/auth/login" `
  -ContentType 'application/json' `
  -Body '{"username":"demo","password":"Demo_Passw0rd!"}'
$login | ConvertTo-Json

$verify = Invoke-RestMethod -Method Post `
  -Uri "$base/auth/challenges/$($login.challenge_id)/verify" `
  -ContentType 'application/json' -Body '{"code":"123456"}'
$verify.access_token

Invoke-RestMethod "$base/.well-known/openid-configuration" | ConvertTo-Json
Invoke-RestMethod "$base/.well-known/jwks.json" | ConvertTo-Json -Depth 4
```

Decode the token payload (the header carries `alg=RS256` and a `kid` that must
match the JWKS):

```powershell
$payload = $verify.access_token.Split('.')[1]
$payload = $payload.Replace('-', '+').Replace('_', '/')
while ($payload.Length % 4) { $payload += '=' }
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) |
  ConvertFrom-Json
```

The signing key is generated once and stored in the `auth-keys` volume. A
restart keeps both the `kid` and existing tokens valid. To run outside Docker,
set `DATABASE_URL`, `KEYS_PATH` (for example `./keys`), and
`ASPNETCORE_URLS=http://localhost:8081` before `dotnet run --project src/SmeDemo.Auth`.

## Resource API

`api` runs on port 8082 and verifies the auth service's JWTs through its OIDC
metadata; it never holds a signing key. Every route under `/api/v1` requires
`Authorization: Bearer <token>`; `/healthz` and `/readyz` are anonymous, and the
Development-only `/openapi/v1.json` is anonymous too.

Take a token with the auth flow above, then:

```powershell
$api = 'http://localhost:8082'
$headers = @{ Authorization = "Bearer $($verify.access_token)" }

Invoke-RestMethod "$api/api/v1/me" -Headers $headers
Invoke-RestMethod "$api/api/v1/me/cards" -Headers $headers

$cards = Invoke-RestMethod "$api/api/v1/me/cards" -Headers $headers
$cardId = $cards.items[0].id

# First page (limit defaults to 20, maximum 100).
$page1 = Invoke-RestMethod "$api/api/v1/me/cards/$cardId/transactions?limit=5" -Headers $headers
$page1.next_cursor

# Next page: the cursor is opaque and must be URL-encoded.
$cursor = [uri]::EscapeDataString($page1.next_cursor)
$page2 = Invoke-RestMethod "$api/api/v1/me/cards/$cardId/transactions?limit=5&cursor=$cursor" -Headers $headers

# Card artwork comes back through the API with a cache header.
Invoke-WebRequest "$api/api/v1/cards/$cardId/art" -Headers $headers -OutFile card.svg

# The profile screen's phone number comes from auth, not the API token.
Invoke-RestMethod 'http://localhost:8081/auth/userinfo' -Headers $headers
```

Expected shapes: `/me` returns `{ id, org, name }`; cards are ordered by
`display_order`; transactions are newest first and `next_cursor` is `null` on the
last page. Errors use the ProblemDetails envelope from `requirements.md` §7.2.

### API rate limits

Every `/api/v1` operation has one fixed-window budget per user and endpoint:
120 requests per minute by default (`API_RATE_LIMIT_PERMIT`,
`API_RATE_LIMIT_WINDOW`). The partition key is the token's `sub` plus the
endpoint name, so one hot endpoint does not consume another's budget. The budget
is checked after authentication, so an anonymous call still gets its normal
`401`, never a `429`. Exhaustion returns `429 RATE_LIMITED` with a numeric
`Retry-After`; `/healthz` and `/readyz` are outside the `/api/v1` group and are
never rate limited. Like the auth budgets, the limiter is in-memory and per
process.

### Storage, seeding, and sync

`azurite` provides Table Storage (transactions) and Blob Storage (`card-art`) on
the internal network only. The API seeds three cards and their SVG artwork at
start-up; every write is an idempotent upsert, so a restart or a second run does
not duplicate data. Set `SEED_ENABLED=false` to skip card seeding (the API tests
do).

Transactions are not seeded. `IssuerSyncService` pulls them from `issuer-mock` at
start-up and then every `ISSUER_SYNC_INTERVAL` (default `5m`; `10s` is useful for
testing). It validates the whole card before writing, upserts by row key, and
records the result in `app.issuer_sync`, one row per card. `ISSUER_SYNC_ENABLED=false`
does not register the worker at all.

Inside Compose the connection string must name the endpoints explicitly:

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;...;BlobEndpoint=http://azurite:10000/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;
```

`UseDevelopmentStorage=true` is only for running the API on the host against a
local Azurite.

### Mock issuer

`issuer-mock` is a separate ASP.NET Core service on port 8084. It serves the
embedded fixture in `tools/IssuerMock/fixtures/transactions.json`; the fixture is
the demo contract, so changing it changes the wallet and the acceptance
screenshots (requirements §13.4).

```powershell
# Liveness.
Invoke-RestMethod http://localhost:8084/healthz

$cardId = 'c1000000-0000-4000-8000-000000000001'
$key = 'demo-issuer-key'
Invoke-RestMethod "http://localhost:8084/issuer/v1/cards/$cardId/transactions" `
  -Headers @{ 'X-Api-Key' = $key } | ConvertTo-Json -Depth 5
```

A missing or wrong `X-Api-Key` returns 401 `UNAUTHORIZED`; an unknown card
returns 404 `CARD_NOT_FOUND`. `ISSUER_BASE_URL` and `ISSUER_API_KEY` on `api`
must agree with the service.

Check sync state and recent log lines:

```powershell
docker exec sme-demo-mssql-1 /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U sa -P "Local_Dev_Only_123!" -C -d sme_api `
  -Q "SELECT CardId, LastStatus, LastSyncedAt, LastError FROM app.issuer_sync ORDER BY CardId"

docker compose logs api | Select-String 'Issuer sync'
```

On a fresh volume, expect three `ok` rows and 14/13/13 transactions for the three
cards. While the issuer is down, expect `failed` rows, a handled warning in the
API log, and unchanged transaction responses.

### Mock SMS provider

`sms-mock` is a separate ASP.NET Core service on port 8083. `auth` posts every
verification code to `SMS_PROVIDER_URL`, which Compose defaults to
`http://sms-mock:8083`. The mock does not deliver anything: it validates the
message envelope and logs it, so the log is where the demo OTP comes from.

```powershell
# Liveness.
Invoke-RestMethod http://localhost:8083/healthz

# Send a message by hand.
$response = Invoke-WebRequest -Method Post -Uri http://localhost:8083/messages `
  -ContentType 'application/json' `
  -Body '{"to":"+61400000000","text":"Your SME Demo verification code is 123456. It expires in 5 minutes."}'
$response.StatusCode        # 202
$response.Content           # sms-<32 lowercase hex>

# Every accepted message is in the log, in plain text.
docker compose logs sms-mock | Select-String 'SMS to'
```

The request body is `{ "to": ..., "text": ... }`; both are required and trimmed
(`to` up to 32 characters, `text` up to 1600). Invalid input returns `400` with
the standard `VALIDATION_ERROR` envelope; the body itself is capped at 64 KB. Demo mode still sends: the fixed
`123456` is posted to the mock exactly like a random code, so the demo log always
shows `Your SME Demo verification code is 123456`. With `DEMO_MODE=false` the
code is random and must be read from the same log before calling verify.

If `sms-mock` is stopped, `auth` stays healthy but login and resend return `503`
with code `UPSTREAM_UNAVAILABLE` and a readable message
("The SMS provider is unavailable. Try again shortly."). Start it again and login
recovers. A failed login does not create a challenge; a failed resend keeps the
already-updated challenge, so the next attempt has to wait for the 60 second
cooldown.

### Database logins (acceptance 15)

The one-shot `db-grants` service runs `db/bootstrap/app-logins.sql` after both
migrators finish. It creates `sme_api_app` and `sme_auth_app` from the passwords
in `.env` (`SME_API_DB_PASSWORD`, `SME_AUTH_DB_PASSWORD`) and grants each only
`db_datareader` and `db_datawriter` in its own database. The migrators keep using
`sa`; the applications never do.

Confirm that the API login cannot execute DDL:

```powershell
docker exec sme-demo-mssql-1 /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U sme_api_app -P "Api_App_Dev_123!" -C `
  -d sme_api -b -Q "CREATE TABLE dbo.ddl_probe (id int)"
```

Expected: `CREATE TABLE permission denied in database 'sme_api'.` and a non-zero
exit code.

### HTTPS metadata flag

`AUTH_REQUIRE_HTTPS_METADATA` defaults to `true`. Compose sets it to `false` for
the API only because `auth` is reached over plain HTTP on the internal network;
this is a development setting, not something to carry into a deployed
environment. If the metadata/JWKS fetch fails at the transport layer, the API
returns `503 UPSTREAM_UNAVAILABLE` with `Retry-After: 5` and logs a
metadata/connection error; it does not mislabel the caller's token as invalid.
Signature, issuer, audience, expiry, and algorithm failures still return `401`.
Metadata is cached, so a stale cache can still produce `401 INVALID_TOKEN` for an
unknown key until it refreshes; restarting `api` discards the cache.

### Forwarded headers

`AUTH_TRUST_FORWARDED_HEADERS` defaults to `false`. Compose sets it to `true`
because nginx is the only intended browser ingress and appends `X-Forwarded-For`, so the per-IP
budgets key on the browser address instead of the proxy's. When true the code
trusts a single forwarded hop and clears the default known proxies/networks,
because the Compose network address is not loopback.

Only enable it when a trusted proxy is the only path to `auth`. A caller that can
reach `auth` directly can spoof the header and evade the per-IP budgets, and the
demo still publishes `127.0.0.1:8081` for debugging, so that is true locally. A
production deployment must instead scope trusted proxies/networks to the exact
ingress and must not expose `auth` directly.

## OpenAPI document

[`openapi/v1.json`](../openapi/v1.json) is a committed build artefact, not a file
maintained by hand.

The API references `Microsoft.Extensions.ApiDescription.Server` in
`src/SmeDemo.Api/SmeDemo.Api.csproj`. During `dotnet build` that package starts
the API entry point with the `GetDocument.Insider` entry assembly and inert
options, reads the document from the service container, and writes it to
`openapi/v1.json`. `Program.cs` detects that entry assembly and calls
`ApiOptions.ForOpenApiDocumentGeneration()`, so the build-time run contacts no
database, signing key, or network endpoint. The output directory and file name
come from `OpenApiDocumentsDirectory` and `OpenApiGenerateDocumentsOptions` in
the project file.

The document is produced from endpoint metadata. `BearerSecuritySchemeTransformer`
declares the `Bearer` scheme, and `EndpointSecurityTransformer` adds the
requirement to every operation that is not explicitly `AllowAnonymous`;
`/healthz` and `/readyz` are therefore documented without a security requirement.
`ServerDocumentTransformer` writes a single `servers` entry from
`PUBLIC_BASE_URL`, so a generated client does not have to guess the host; the
committed document carries the build-time default `http://localhost:8082`.
Every `/api/v1` operation also declares the `429` response, and `/readyz`
declares both its `200` and `503` readiness bodies.

Regenerate and check the committed file:

```powershell
dotnet build src/SmeDemo.Api/SmeDemo.Api.csproj --configuration Release
git diff --exit-code -- openapi/
```

A stale file shows up as a git diff. CI builds the solution and runs the same
diff, so a route or contract change cannot ship without the regenerated document.

At runtime, `GET /openapi/v1.json` is mapped only in Development
(`app.MapOpenApi().AllowAnonymous()`), so it is not part of production
behaviour. The served document uses the same explicit `servers` entry, so it
matches the committed file exactly;
[`OpenApiContractTests`](../tests/SmeDemo.Api.Tests/OpenApiContractTests.cs)
compares the two documents, including `servers`, and fails if either drifts.

To consume the contract, point a client generator at the committed file, or at
the Development endpoint while the API is running. Both describe the same
operations; the token itself comes from `auth` and is not embedded in the
document. Commit regenerated clients with the contract change.

## Web front-end

The browser UI lives in `web/`. It is a React 18 + TypeScript + Vite SPA with
Tailwind CSS, built to static assets and served by nginx in Compose. The browser
only ever talks to port 8080: nginx serves the SPA and reverse-proxies `/auth/*`
to `auth:8081` and `/api/*` to `api:8082`. Requests in the source are always
same-origin relative paths, so there is no base URL or CORS configuration to
maintain.

### Local development

```powershell
cd web
npm ci
npm run dev
```

Open `http://localhost:5173`. The Vite dev server proxies `/auth` to
`http://localhost:8081` and `/api` to `http://localhost:8082`, so `auth` and
`api` must be running (`docker compose up -d auth api`) for login and data to
work. `web/public/config.js` is the committed development default for the demo
hint.

```powershell
npm run build        # tsc --noEmit && vite build, output in web/dist
npm test             # Vitest + Testing Library
npx tsc --noEmit     # type check on its own
```

### Visual Studio 2026

`web/SmeDemo.Web.esproj` is a JavaScript Project System wrapper around
`package.json`, and `SmeDemo.slnx` lists it under `/web/`, so Solution Explorer
shows the SPA next to the services. The project adds no build logic of its own:
Build, Test and Start map to `npm run build`, `npm test` and `npm run dev`.

The .NET CLI skips `.esproj` entries, so `dotnet build SmeDemo.slnx` and the CI
`.NET` job are unchanged; the JavaScript toolchain still runs only through npm or
Visual Studio.

To work on the front end in the main solution window:

1. Start the back end that Vite proxies to: `docker compose up -d auth api`, or
   F5 on the Compose project.
2. Right-click **SmeDemo.Web** in Solution Explorer and choose **Debug > Start
   New Instance**, or set it as the startup project and press F5. A console runs
   `npm run dev`; open `http://localhost:5173` if no browser opens.
3. **Build > Build Solution** runs `tsc --noEmit && vite build`. `SmeDemo.Web`
   also declares `Vitest` as its test framework, so Test Explorer can discover
   the files under `web/src`.

F5 on the Compose project does not start the Vite dev server: the Compose `web`
service serves the pre-built SPA from nginx on 8080. Use both if you want the
container debugger and hot reload at the same time.

For front-end-only work, open `web/SmeDemo.Web.slnx` instead: it contains just
the SPA and loads faster than the full solution. Visual Studio Code needs no
project file at all — `npm ci` and the scripts above are enough, and that is what
CI uses.

The project pins `Microsoft.VisualStudio.JavaScript.Sdk/1.0.6537497`, the version
shipped with Visual Studio 2026 18.11. If a Visual Studio update stops the project
from loading, set the version to the one installed under
`%ProgramData%\Microsoft\VisualStudio\Packages\Microsoft.VisualStudio.JavaScript.SDK_*`.

### Container build and runtime configuration

```powershell
docker compose up -d --build web
```

The Dockerfile is multi-stage: `node:22-bookworm-slim` runs `npm ci` and the
build, then the static output is copied into `nginx:1.31-alpine`, which runs as
the unprivileged `nginx` user on port 8080. npm packages are fetched during
`docker compose build` only; the runtime image makes no outbound requests.

`/config.js` is generated when the container starts by
`web/nginx/40-runtime-config.sh` from `DEMO_MODE`, `DEMO_USERNAME`,
`DEMO_PASSWORD`, and `DEMO_OTP`. It is written to the `/tmp` tmpfs because the
root filesystem is read-only. `DEMO_MODE` must be exactly `true` for the file
to contain credentials. To change the demo hint:

```powershell
# Edit .env, then recreate only web so the entrypoint runs again.
docker compose up -d --force-recreate web
```

`/config.js` is served with `Cache-Control: no-store`; `/assets/*` is immutable
because Vite fingerprints those files; `/index.html` and the SPA fallback are
`no-cache`. The nginx server block is rendered at container start from
`web/nginx/default.conf.template` into `/tmp/conf.d/default.conf` by
`30-upstream-config.sh`, using `UPSTREAM_AUTH` (default `http://auth:8081`),
`UPSTREAM_API` (default `http://api:8082`), and `NGINX_RESOLVER` (default
`127.0.0.11`, Docker's embedded DNS). That keeps the Compose stack unchanged
while the Azure deployment supplies environment-specific FQDNs and the platform
resolver; recreating `auth` or `api` does not leave nginx pointing at a stale
container IP. auth's own liveness probe remains `/healthz` on 8081 and api's
Compose healthcheck is `/readyz` on 8082; the proxied application routes under
`/auth/` and `/api/` are forwarded unchanged.

Session behaviour: the token is kept in `sessionStorage` and attached by a
single fetch wrapper. An authenticated 401 clears the session and redirects to
`/login?reason=expired`; a 401 from login, verify, or resend is only a form
error. Card artwork is fetched with the bearer token and converted to a blob
URL, because an `<img>` request cannot carry an `Authorization` header.

### Card transactions

The card detail screen loads
`/api/v1/me/cards/{id}/transactions?limit=10` and appends pages with the opaque
`next_cursor` (URL-encoded). Ten is deliberate: the seeded feed has 14, 13, and
13 transactions per card, so every card has a second page and `Load more` can be
demonstrated without changing the fixture. Rows keep the API's newest-first
order and are grouped by local calendar date only for display. Appends are
de-duplicated by transaction id; a failed append keeps the loaded rows and
retries the same cursor. The empty state is exactly `No transactions yet.`, and
a `Pending` badge appears only on pending rows.

The wallet restores its scroll position after returning from a card: the offset
is saved on scroll and immediately before a tile is followed, kept in
`sessionStorage` under `sme.wallet.scroll`, restored once after the grid renders,
and removed on sign-out or session expiry. It is never stored in `localStorage`.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main`, every pull request, and
on demand. It has five jobs:

| Job | Runner / timeout | What it does |
|-----|------------------|--------------|
| `dotnet` | ubuntu, 20 min | checkout; set up .NET from `global.json` (`10.0.404`, `latestFeature`); `dotnet tool restore`; `dotnet build SmeDemo.slnx --configuration Release`; `dotnet test SmeDemo.slnx --configuration Release --no-build`; `dotnet format SmeDemo.slnx --verify-no-changes`; verify `openapi/v1.json` exists and `git diff --exit-code -- openapi/` (the Release build regenerates it); pending-model checks for the API and auth DbContexts; generated-SQL diff for both; removed-package guard (MediatR/AutoMapper); `python3 tools/smoke/check-vulnerabilities.py`; gitleaks full-history secret scan (checkout uses `fetch-depth: 0`). |
| `web` | ubuntu, 15 min | Node 22, `npm ci`, `npm run build`, `npm test`. |
| `compose` | ubuntu, 5 min | copy `.env.example` to `.env`, then `docker compose config -q`. |
| `images` | ubuntu, 50 min | build six images from the repository root: api, auth, web, sms-mock, issuer-mock, and migrator, each tagged with `${GITHUB_SHA}`; generate one CycloneDX SBOM per image into `$RUNNER_TEMP/sbom` and upload them as artifact `sboms`; scan each image with Trivy 0.76.2 for fixable HIGH/CRITICAL vulnerabilities (`--severity HIGH,CRITICAL --ignore-unfixed --timeout 15m --exit-code 1`); fail on any build or scan error. |
| `compose-smoke` | ubuntu, 45 min | start the minimal stack, wait for the one-shot containers and health, run `tools/smoke/compose-smoke.sh`, dump diagnostics on failure, and always tear down with volumes. |

Both scanner downloads — gitleaks and Trivy — are pinned to an exact version and
verified by SHA-256 before the binary runs.

A second workflow, `.github/workflows/security-scan.yml`, runs the same
image-build and Trivy gate on a weekly schedule (and on demand). It exists
because a new base-image CVE appears without a commit. The full change, weekly,
and release cadence is in [`delivery.md`](delivery.md).

The `dotnet`, `web`, and `compose` jobs need no SQL Server, Azurite, or SMS
container: the API tests replace the repositories and storage with fakes, the
auth tests use EF InMemory, and the frontend tests run in jsdom.

### HTTP test hosts

Both service test projects boot the real ASP.NET Core host through
`WebApplicationFactory`, and both pass configuration with per-factory
`UseSetting` values instead of mutating process environment variables, so
factories can run with independent settings in the same test run.

- `tests/SmeDemo.Api.Tests/ApiTestFactory.cs` replaces the card repository,
  transaction store, blob store, and all readiness probes with fakes, and
  supplies a local RSA key instead of fetching auth metadata. Its constructor
  can lower `API_RATE_LIMIT_PERMIT`/`API_RATE_LIMIT_WINDOW` for a test.
- `tests/SmeDemo.Auth.Tests/AuthTestFactory.cs` replaces the SQL Server
  DbContext with EF InMemory but keeps the real seeder, swaps the SMS provider
  for a recording fake, and installs a manual clock for challenge TTL and resend
  cooldown. It accepts `AUTH_RATE_LIMIT_*` and other host settings per factory.
  Auth reads its options through `IConfiguration`, so the same values work from
  environment variables or another provider outside tests.

The Azure Pipelines equivalent — the same Build gates plus the Test, UAT, and
Production deployment stages — is described in
[`azure-pipelines.md`](azure-pipelines.md). Both CI systems also validate the
deployment assets without an Azure subscription: `az bicep build --file
infra/main.bicep --stdout` and `bash -n` over `tools/deploy/*.sh` and
`web/nginx/*.sh`. The scripts it runs are
[`deploy.sh`](../tools/deploy/deploy.sh) and
[`rollback.sh`](../tools/deploy/rollback.sh); GitHub Actions stays the fast
pull-request loop, and Azure Pipelines is the formal release path.

### Format gate

CI runs `dotnet format SmeDemo.slnx --verify-no-changes` on Linux. The command may
print a harmless warning that `docker-compose.dcproj` has no associated language;
it still exits 0. Any non-zero exit fails the job.

### OpenAPI gate

The Release build runs the API project's build-time generator and rewrites
`openapi/v1.json`. CI then checks that the file exists and that
`git diff --exit-code -- openapi/` is clean. If the document is stale, the job
fails and the error message tells you to rebuild
`src/SmeDemo.Api/SmeDemo.Api.csproj` in Release and commit the regenerated file.
The document is not edited by hand.

### Dependency and secret scans

The vulnerability gate is not raw `dotnet list package --vulnerable`, because that
command exits 0 even when it reports findings. CI runs:

```bash
python3 tools/smoke/check-vulnerabilities.py
```

The script discovers every project (currently 16) under `src/`, `tests/`, and
`tools/` (recursively, skipping `bin/` and `obj/`; `docker-compose.dcproj` is
outside those directories). For each project it runs `dotnet list <project> package
--vulnerable --include-transitive --format json --no-restore`, parses the JSON,
and exits non-zero if any advisory is present, the command fails, or the output
cannot be parsed. It uses only the Python standard library, so no third-party
package is installed. It expects the Release build/restore to have run first.

The secret gate uses a pinned gitleaks release, downloaded by CI and verified by
SHA-256 before it runs:

| Variable | Value |
|----------|-------|
| `GITLEAKS_VERSION` | `8.30.1` |
| `GITLEAKS_SHA256` | `551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb` |
| Linux x64 asset | `https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz` |

The scan command is:

```bash
gitleaks detect --source . --redact --config .gitleaks.toml --exit-code 1
```

CI checks out the **full history** (`fetch-depth: 0`), so the scan covers every
commit, not just the working tree. On a shallow clone, run
`git fetch --unshallow` first, or use `--no-git` to scan only the working tree (a
narrower, local-only check). `.gitleaks.toml` extends the default rule set and
allowlists only the Azurite emulator's documented well-known key plus local
build/tool caches (`.vs/`, `.tmp-tools/`, `bin/`, `obj/`, `node_modules/`).

**Container images.** The NuGet scan covers the application dependency graph;
the `images` job scans the artefact that is actually promoted. After building
the six images it generates one CycloneDX SBOM per image
(`trivy image --format cyclonedx`) and uploads them as the `sboms` artifact
before the vulnerability gate, so a failing scan still leaves the inventory
behind. It then scans the same images and fails on every fixable `HIGH` or
`CRITICAL` finding:

```bash
trivy image --scanners vuln --timeout 15m \
  --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 <image>
```

`--ignore-unfixed` means unfixed findings are filtered out of this gate: it fails
only on findings a rebuild can fix, and it is not an inventory of unfixed CVEs.
Run a separate scan without the flag if that inventory is needed for the risk
register. On 2026-11-14 all six images scanned clean locally. Like gitleaks, Trivy is
downloaded from a pinned release and checksum-verified:

| Variable | Value |
|----------|-------|
| `TRIVY_VERSION` | `0.76.2` |
| `TRIVY_SHA256` | `bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf` |
| Linux x64 asset | `https://github.com/aquasecurity/trivy/releases/download/v0.76.2/trivy_0.76.2_Linux-64bit.tar.gz` |

**Dependency updates.** [`.github/dependabot.yml`](../.github/dependabot.yml)
runs weekly on Monday and groups updates so a patch wave produces one pull
request per ecosystem rather than one per package. It covers NuGet (central
package management), npm under `/web`, `github-actions`, and Docker for the root
Compose file plus every Dockerfile directory (`/src/SmeDemo.Api`,
`/src/SmeDemo.Auth`, `/web`, `/tools/IssuerMock`, `/tools/SmsMock`, and
`/tools/Migrator`). Every base image is pinned by tag and digest in the
Dockerfiles and `docker-compose.yml`; Dependabot moves the tag and digest
together, and a digest-only change is how a rebuilt base image arrives.

Major runtime jumps are deliberately excluded from the Docker entries: moving
`dotnet` 10 → 11, `node` 22 → 25 or `nginx` 1.x → 2.x changes the target
framework, the lock file's native binaries, or the nginx configuration contract.
Those are separate work items that must pass the full build, image scan and
Compose smoke gates. Patch and digest updates inside the current major still
arrive weekly, and a major action upgrade is reviewed in a pull request that runs
the updated workflow.

### Compose smoke test

The `compose-smoke` job prepares `.env` and starts the minimal set with `--build`:

```bash
docker compose up -d --build \
  mssql azurite db-migrator db-migrator-auth db-grants auth api
```

It waits for `auth` and `api` to be healthy and for `db-migrator`,
`db-migrator-auth`, and `db-grants` to exit 0, then runs
`bash tools/smoke/compose-smoke.sh`. The script prints `PASS`/`FAIL` for each
check:

1. `auth` returns 200 from `/healthz`.
2. `api` returns 200 from `/readyz`, proving SQL Server, Blob Storage, and auth
   metadata are reachable.
3. `POST /auth/login` returns 200 with a `challenge_id`.
4. `POST /auth/challenges/<id>/verify` returns 200 with an `access_token`.
5. `GET /api/v1/me` with the token returns 200 with the expected `org` and a
   non-empty `name`.
6. `GET /api/v1/me` without a token returns 401 with
   `error.code = UNAUTHENTICATED`.

On failure the job dumps `docker compose ps -a` and
`docker compose logs --no-color`. The teardown step runs unconditionally:
`docker compose down -v --remove-orphans`.

### Container runtime hardening

`docker-compose.yml` applies a shared hardening anchor to the runtime services
(`api`, `auth`, `web`, `sms-mock`, `issuer-mock`, the migrator containers, and
`azurite`): a read-only root filesystem, `no-new-privileges:true`,
`cap_drop: [ALL]`, and a 64 MiB `tmpfs` at `/tmp`.
SQL Server keeps `no-new-privileges` plus its resource limit because its
entrypoint must prepare the data volume; `db-grants` uses the same two controls.
Every service also declares CPU and memory limits (for example `api` 1 CPU /
512 MiB, `web` 0.5 CPU / 128 MiB, `mssql` 2 CPU / 2 GiB). The one-shot services
keep their `restart: "no"` behaviour.

The .NET runtime images are Microsoft chiseled `-extra` images: no shell,
package manager, or `curl`. `-extra` is required because `Microsoft.Data.SqlClient`
rejects invariant globalization; it provides ICU/tzdata without a shell. Auth
creates `/keys` with the application user's ownership in an SDK stage, and the
Compose healthchecks use the in-image
`SmeDemo.HealthProbe` instead of `curl`. A read-only root filesystem means a
process that tries to write outside `/tmp` or a mounted volume fails; show that
without a shell with `docker compose cp .env.example api:/nope` (fails), and
confirm the `/tmp` tmpfs with `docker inspect`. Docker's copy path refuses writes
into any path in a read-only-root container, so copying into `/tmp` fails too; it
is not a way to prove tmpfs writability. Use `docker inspect`, `docker compose
logs`, or `dotnet` for the other checks; `docker compose exec api sh` is expected
to fail because there is no shell.

The web runtime is `nginx:1.31-alpine` (digest-pinned), runs as the unprivileged
`nginx` user on port 8080, and publishes only `127.0.0.1:8080:8080`. The build
removes `nginx-module-xslt` and `libxslt` because the configuration never loads
XSLT. `/config.js` is generated into `/tmp` and served with
`Cache-Control: no-store`. `web` is the one runtime image that still has a
shell and uses `wget` in its healthcheck. Visual Studio debugging relaxes the
CLI hardening in `docker-compose.vs.debug.yml` (`read_only: false` plus
`SYS_PTRACE`), which `docker compose up` never reads, so the CLI stack stays
hardened; the `base` stage still carries the probe for Fast mode. For failure
triage, see the write-failure rows in [Troubleshooting](#troubleshooting); the
operational procedures are in [`runbook.md`](runbook.md), and the rationale is
in [ADR-019](reference/adr/ADR-019-chiseled-runtime-images-and-health-probe.md)
and [ADR-018](reference/adr/ADR-018-observability-and-supply-chain.md).

### Observability

Both services register OpenTelemetry instrumentation for ASP.NET Core requests,
outgoing `HttpClient` calls, EF Core, runtime metrics, and the Azure Blob/Table
activity sources. Exporters are opt-in: with no endpoint configured the signals
are collected and dropped, and Compose ships no collector. `LOG_FORMAT=json`
switches the console logger to one JSON object per line; Compose sets it for
`api` and `auth`, while a local `dotnet run` keeps the human-readable format.
Every response carries `X-Request-Id`: a valid inbound value (1–64 characters
from `[A-Za-z0-9._:-]`) is echoed, otherwise the trace id is used. The same value
is logged as `request_id`, which is how a failing response is tied back to the
JSON log line and its trace. The full signal list, resource attributes, and the
local collector recipe are in [`observability.md`](observability.md).

### What you can verify locally

The build, test, format, schema, OpenAPI, dependency-scan, secret-scan, web, and
Compose-configuration checks reproduce without the runner:

```powershell
dotnet tool restore
dotnet build SmeDemo.slnx --configuration Release
dotnet test SmeDemo.slnx --configuration Release --no-build
dotnet format SmeDemo.slnx --verify-no-changes
# Expected: the harmless docker-compose.dcproj warning; exit code 0.

dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Infrastructure --startup-project src/SmeDemo.Infrastructure
dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Auth --startup-project src/SmeDemo.Auth

dotnet tool run dotnet-ef migrations script --idempotent `
  --project src/SmeDemo.Infrastructure --startup-project src/SmeDemo.Infrastructure `
  --output db/migrations/api/sme_api.sql
dotnet tool run dotnet-ef migrations script --idempotent `
  --project src/SmeDemo.Auth --startup-project src/SmeDemo.Auth `
  --output db/migrations/auth/sme_auth.sql
git diff --exit-code -- db/migrations

dotnet build src/SmeDemo.Api/SmeDemo.Api.csproj --configuration Release
git diff --exit-code -- openapi/

python3 tools/smoke/check-vulnerabilities.py

cd web
npm ci
npm run build
npm test

cd ..
Copy-Item .env.example .env -Force   # only in a scratch checkout
docker compose config -q
```

With the full stack running (`docker compose up -d --build`), these runtime
checks exercise the hardening and the observability contracts:

```powershell
# Read-only root: copying into it fails; /tmp is the mounted tmpfs.
docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' (docker compose ps -q api)
docker inspect --format '{{json .HostConfig.Tmpfs}}' (docker compose ps -q api)
docker compose cp .env.example api:/nope          # expected to fail (read-only)
# Docker cp also refuses writes under /tmp when the rootfs is read-only.
docker compose cp .env.example api:/tmp/probe     # expected to fail

# The .NET images have no shell; this must fail with "no such file or directory".
docker compose exec -T api sh -c 'true'

# A valid X-Request-Id is echoed back; an invalid one is replaced by the trace id.
$response = Invoke-WebRequest http://localhost:8082/healthz `
  -Headers @{ 'X-Request-Id' = 'demo-check-1' }
$response.Headers['X-Request-Id']                       # demo-check-1

# /config.js must never be cached.
$config = Invoke-WebRequest http://localhost:8080/config.js
$config.Headers['Cache-Control']                        # no-store

# Compose sets LOG_FORMAT=json for api and auth.
docker compose logs --no-log-prefix --tail 1 api | ConvertFrom-Json
docker compose logs --no-log-prefix api |
  Select-String 'request_id' | Select-Object -Last 1 |
  ForEach-Object { $_.Line | ConvertFrom-Json } |
  Select-Object request_id, TraceId, SpanId
```

The SBOM generation and Trivy image scan do not run locally in this list; they
are part of the CI `images` job, which scans the Linux-built images. All six
images scanned clean under the same gate on 2026-11-14.

The removed-package guard is the CI equivalent of:

```powershell
Select-String -Path (Get-ChildItem -Recurse -File -Include *.csproj,*.props).FullName `
  -Pattern '<Package(Reference|Version) Include="(MediatR|AutoMapper)"'
```

For the secret scan, install the pinned gitleaks 8.30.1 binary for your platform
from <https://github.com/gitleaks/gitleaks/releases/tag/v8.30.1>, verify its
checksum against the release, then run the same command:

```bash
gitleaks detect --source . --redact --config .gitleaks.toml --exit-code 1
```

CI uses the Linux x64 asset and SHA-256 listed above and fetches the full
history; use the matching asset for your operating system locally. A shallow
clone must be unshallowed first, or the command narrowed with `--no-git` to the
working tree only.

### What runs only in CI today

- **`images`** builds and tags all six application images with `${GITHUB_SHA}`,
  generates a CycloneDX SBOM per image, uploads them as the `sboms` artifact,
  and scans every image for fixable HIGH/CRITICAL vulnerabilities with the
  pinned Trivy release. `docker compose build` locally proves the Dockerfiles on
  your machine, but it is not the same gate: the CI job builds every image, on
  Linux, from a clean checkout, with the repository root as the build context,
  and it scans what was actually built.
- **`compose-smoke`** starts the minimal real stack, waits for the API's
  readiness check, performs the auth login and OTP flow, and calls the API
  through the token. Reproduce it with the minimal `docker compose up -d
  --build` command above, `bash tools/smoke/compose-smoke.sh`, and
  `docker compose down -v --remove-orphans`; the CI job remains the required
  Linux proof.

### Verification status (2026-11-14)

- The full hardened Compose stack, including `web`, started locally on
  2026-11-14; the Compose smoke checks passed and auth login plus API calls work
  through nginx on `http://localhost:8080`.
- `X-Request-Id` echo and the JSON console logs were verified on `api` and
  `auth`; `/config.js` returned `Cache-Control: no-store`; the read-only root
  was inspected and Docker refuses copies into it; the `/tmp` tmpfs is mounted
  with the expected size; and all six images scanned clean under the Trivy gate.
- The five .NET images were rebuilt on chiseled `-extra` bases and checked for
  no shell, no package manager, no `curl`, a working `/healthprobe`, and an
  auth-keys volume owned by `1654:1654`. This was a local real-stack verification
  before the pull request was opened; GitHub Actions run `43566055703` at
  `d020a1e` then passed all five jobs.
- GitHub Actions was also green for PR 3 (`43553676907` at `eb96829` and
  `43560262538` at `bd33c08`); Azure Pipelines has not been queued.

## Everyday commands

```powershell
dotnet tool restore                       # after cloning; installs dotnet-ef
dotnet build SmeDemo.slnx
dotnet test SmeDemo.slnx                  # migrator, auth, application, infrastructure, issuer-mock, API tests
```

With the stack running, check request correlation and JSON logging:

```powershell
# The header is echoed when valid, otherwise replaced by the trace id.
$health = Invoke-WebRequest http://localhost:8082/healthz `
  -Headers @{ 'X-Request-Id' = 'everyday-check-1' }
$health.Headers['X-Request-Id']           # everyday-check-1

# Compose sets LOG_FORMAT=json for api and auth.
docker compose logs --no-log-prefix --tail 1 api | ConvertFrom-Json
```

## Changing a schema

### API schema

1. Change the entity in `src/SmeDemo.Domain`.
2. Add a migration:

```powershell
dotnet tool run dotnet-ef migrations add AddSomething `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure `
  --output-dir Persistence/Migrations
```

3. Read the generated `Up()` before going further. If it drops or rewrites
   something, stop and split the change (see `requirements.md` §8.8).
4. Regenerate the committed script — this is the artefact that gets deployed:

```powershell
dotnet tool run dotnet-ef migrations script --idempotent `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure `
  --output db/migrations/api/sme_api.sql
```

5. Apply it:

```powershell
docker compose run --rm db-migrator
```

### Auth schema

The same pipeline, with the auth project and its own script and migrator:

```powershell
dotnet tool run dotnet-ef migrations add AddSomething `
  --project src/SmeDemo.Auth `
  --startup-project src/SmeDemo.Auth `
  --output-dir Persistence/Migrations

dotnet tool run dotnet-ef migrations script --idempotent `
  --project src/SmeDemo.Auth `
  --startup-project src/SmeDemo.Auth `
  --output db/migrations/auth/sme_auth.sql

docker compose run --rm db-migrator-auth
```

The script is idempotent, so re-running it is safe and should print the same
result. Commit the entity change, the migration, and the regenerated script in
one commit.

## Checking for drift

```powershell
dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure

dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Auth `
  --startup-project src/SmeDemo.Auth
```

It prints `No changes have been made to the model since the last migration` when
the model and migrations agree. Run it before every commit; CI also runs it on
every push and pull request.

## Resetting

```powershell
docker compose down -v
docker compose up -d --build
```

`-v` removes `mssql-data`, `auth-keys`, and `azurite-data`, so this is a full
reset. Do not use it on a database you care about.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `create .env from .env.example` | `.env` missing | copy the example |
| Migrator exits 1 immediately | SQL Server still starting | it retries for `DB_WAIT_SECONDS`; check `docker compose logs mssql` |
| Login failed for user `sa` | password changed after the volume was initialised | `docker compose down -v`, or use the original password |
| Login failed for `sme_api_app` / `sme_auth_app` | `db-grants` did not run, or the `.env` password changed after it did | check `docker compose logs db-grants`; re-run `docker compose up -d db-grants` |
| `has-pending-model-changes` fails | entity edited without a migration | add the migration, do not hand-edit the snapshot |
| Auth exits at start-up | `DEMO_MODE=false` with `DEMO_OTP` set, or no `SMS_PROVIDER_URL` | remove `DEMO_OTP`, set the SMS URL, or enable demo mode |
| Login or resend returns `503` `UPSTREAM_UNAVAILABLE` | `sms-mock` is stopped or `SMS_PROVIDER_URL` is wrong | `docker compose ps sms-mock`, check `docker compose logs auth` and the URL, then `docker compose start sms-mock` |
| Auth cannot write `/keys` | key volume not writable by the app user | `docker compose down -v` to recreate the volume |
| `docker compose exec api sh` fails with `no such file or directory` | the .NET runtime images are chiseled and have no shell | expected: use `docker inspect`, `docker compose logs`, copying files out, or `docker compose exec api dotnet ...`; `web` still has a shell |
| `api` unhealthy or restarts | Azurite/Table/Blob not ready, or SQL login lacks rights | check `docker compose logs api`: the seeder retries 20 times, then stops the host; check `docker compose ps azurite` and `db-grants` |
| `api` unhealthy but `/healthz` returns 200 | `/readyz` cannot reach SQL Server, Blob Storage, or auth metadata | `Invoke-WebRequest -SkipHttpErrorCheck http://localhost:8082/readyz`; the `checks` array names the failed dependency; check `docker compose logs api` |
| Every authenticated `/api/v1` call returns `503` `UPSTREAM_UNAVAILABLE` | auth metadata/JWKS fetch failed at the transport layer | check `docker compose ps auth`, `AUTH_METADATA_URL`, and `AUTH_REQUIRE_HTTPS_METADATA=false` in Compose; the response carries `Retry-After: 5` |
| Valid token returns `401` `INVALID_TOKEN` after auth recovered | `api` still has a stale cached metadata document | restart `api` to discard the cache, or wait for the refresh; signature, issuer, and audience failures are real 401s |
| Login, verify, or resend returns `429` `RATE_LIMITED` | an auth budget is exhausted (defaults: login 20/IP/min and 10/account/5m, verify 60/IP/min, resend 10/IP/min) | wait for `Retry-After`; raise the matching `AUTH_RATE_LIMIT_*` values; restarting `auth` resets the in-memory windows |
| `/api/v1` returns `429` `RATE_LIMITED` | the per-user, per-endpoint API budget is exhausted (default 120/min) | wait for `Retry-After`; raise `API_RATE_LIMIT_PERMIT`/`API_RATE_LIMIT_WINDOW`; restarting `api` resets the in-memory windows |
| `issuer_sync` rows are `failed` | issuer-mock stopped, wrong `ISSUER_API_KEY`, or issuer returned an invalid payload | check `docker compose ps issuer-mock` and `docker compose logs api`; curl the issuer endpoint with the configured key; the API keeps serving the local feed meanwhile |
| Wallet has cards but no transactions after a fresh start | the first sync has not run yet, or it failed | wait for `ISSUER_SYNC_INTERVAL`, check `app.issuer_sync`, and re-run with a short `ISSUER_SYNC_INTERVAL=10s` if needed |
| Issuer returns 401 | `ISSUER_API_KEY` differs between `api` and `issuer-mock` | set one value for both in `.env` and recreate both containers |
| `web` is up but the page is blank | the built assets are missing or the module failed to load | check the browser console and `docker compose logs web`; rebuild with `docker compose up -d --build web` |
| `http://localhost:8080/auth/...` or `/api/...` returns 502 | nginx cannot resolve or reach `auth`/`api` | `docker compose ps auth api`; wait for both to be healthy, then `docker compose restart web` |
| Demo hint is stale or missing | `/config.js` was cached, or the container was not recreated after changing `.env` | reload with dev tools open, then `docker compose up -d --force-recreate web` |
| `web` is healthy but `/config.js` returns 404 | the `/tmp` tmpfs is missing, or the entrypoint did not run `40-runtime-config.sh` | `docker compose exec web sh -c 'ls -l /tmp/config.js'` and `docker compose logs web`; recreate web (`docker compose up -d --force-recreate web`) and confirm the Compose `tmpfs: /tmp` entry is present |
| A request larger than 1 MiB returns `413` through the real port but not in `WebApplicationFactory` tests | TestServer does not enforce Kestrel limits; the real host enforces `MAX_REQUEST_BODY_BYTES` (default 1 MiB) and nginx enforces `client_max_body_size 1m` | reproduce against the container, not the test host; raise `MAX_REQUEST_BODY_BYTES` and the nginx limit together if a larger body is legitimate |
| A service fails to write a file that used to work | the root filesystem is read-only; only `/tmp` and mounted volumes are writable | write scratch data under `/tmp` (or add a volume deliberately); confirm with `docker inspect --format '{{.HostConfig.ReadonlyRootfs}}'`; the Visual Studio debug override is the only relaxed path |
| `nginx: [emerg] open() "/tmp/conf.d/default.conf" failed` or `web` exits at start | the server-block renderer ran but produced nothing; `UPSTREAM_AUTH`/`UPSTREAM_API`/`NGINX_RESOLVER` were rejected, or the `/tmp` tmpfs is missing | `docker compose logs web`; the renderer prints `30-upstream-config.sh: ERROR: ...` for a bad value; confirm the Compose `tmpfs: /tmp` entry and recreate web |
| `docker compose build web` fails on `npm ci` | no network during the image build | connect to the registry, or run `cd web; npm ci` locally first to warm the npm cache |

## What is not here yet

The web UI is implemented for sign-in with OTP, the card wallet with artwork,
the card summary plus its paginated, date-grouped transaction list (pending
badges, `Load more`, retry on append failures, wallet scroll restoration), and
the profile page. CI builds and tests both halves on every push and pull request.
A real identity provider is not here yet (requirements §10.6); the demo runs
from Compose on one machine. The Azure deployment is implemented (Container
Apps; see [`azure-pipelines.md`](azure-pipelines.md) and ADR-020) but has not
yet been executed against Azure. The auth
issuer, the resource API, and the mock issuer are implemented with their own
databases, migrations, idempotent scripts, and tests. The mock SMS provider is a
stateless stand-in with its own contract tests. The API pulls transactions from
`issuer-mock` via `IssuerSyncService`; issuer failures degrade to the last good
local feed rather than failing requests. `auth` posts codes to `sms-mock`; an SMS
failure degrades login to a readable 503 rather than a 500.
