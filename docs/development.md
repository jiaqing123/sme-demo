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
src/SmeDemo.Application            MediatR handlers, DTOs, validators, port interfaces
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
tests/IssuerMock.Tests             fixture contract, auth and ordering tests
tests/SmsMock.Tests                SMS mock HTTP contract tests
tests/SmeDemo.Application.Tests    handler and validator unit tests
tests/SmeDemo.Api.Tests            integration tests through WebApplicationFactory
tests/SmeDemo.Auth.Tests           challenge-rule and token-claim tests
tests/SmeDemo.Infrastructure.Tests IssuerSyncService tests against fake ports/InMemory EF
docker-compose.yml                 SQL Server, migrators, grants, Azurite, sms-mock, issuer-mock, auth, API
docker-compose.dcproj              Visual Studio Compose project (F5 debugging of auth and API)
docs/                              requirements, licensing, workshop
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

### F5 fails before any container exists

Visual Studio reports a failed Compose launch as a generic debugger error,
typically `Unable to start debugging. Operation not supported. Unknown error:
0x80070057.`, and its output pane is not always populated. Run the stack from
the CLI first to see the real message:

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

If the containers do come up but the popup persists, the container debugger
bits in your profile are stale, which is a known Visual Studio problem. Delete
them so the next F5 downloads current ones:

```powershell
Remove-Item -Recurse -Force "$env:USERPROFILE\vsdbg"
```

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
metadata; it never holds a signing key. Every route except `/healthz` requires
`Authorization: Bearer <token>`.

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
environment. If metadata retrieval fails, the API rejects every token and its
log shows a metadata/connection error.

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
build, then the static output is copied into `nginx:1.27-alpine`. npm packages
are fetched during `docker compose build` only; the runtime image makes no
outbound requests.

`/config.js` is generated when the container starts by
`web/nginx/40-runtime-config.sh` from `DEMO_MODE`, `DEMO_USERNAME`,
`DEMO_PASSWORD`, and `DEMO_OTP`. `DEMO_MODE` must be exactly `true` for the file
to contain credentials. To change the demo hint:

```powershell
# Edit .env, then recreate only web so the entrypoint runs again.
docker compose up -d --force-recreate web
```

`/config.js` is served with `Cache-Control: no-store`; `/assets/*` is immutable
because Vite fingerprints those files; `/index.html` and the SPA fallback are
`no-cache`. The image also proxies with Docker's embedded DNS
(`resolver 127.0.0.11`), so recreating `auth` or `api` does not leave nginx
pointing at a stale container IP. auth's own liveness probe remains `/healthz`
on 8081; the proxied application routes under `/auth/` are forwarded unchanged.

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
on demand:

- **.NET build and tests** — .NET 10, `dotnet tool restore`, a Release build and
  `dotnet test`, then `has-pending-model-changes` for both DbContexts.
- **Web build and tests** — Node 22, `npm ci`, `npm run build`, `npm test`.

Neither job needs SQL Server, Azurite, or any other container: the API tests
replace the repositories and storage with fakes, the auth tests use EF InMemory,
and the frontend tests run in jsdom. The commands work locally too, so a red job
can be reproduced without the runner:

```powershell
dotnet tool restore
dotnet build SmeDemo.slnx --configuration Release
dotnet test SmeDemo.slnx --configuration Release --no-build
dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Infrastructure --startup-project src/SmeDemo.Infrastructure `
  --configuration Release --no-build
cd web
npm ci
npm run build
npm test
```

## Everyday commands

```powershell
dotnet tool restore                       # after cloning; installs dotnet-ef
dotnet build SmeDemo.slnx
dotnet test SmeDemo.slnx                  # migrator, auth, application, infrastructure, issuer-mock, API tests
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
| `api` unhealthy or restarts | Azurite/Table/Blob not ready, or SQL login lacks rights | check `docker compose logs api`: the seeder retries 20 times, then stops the host; check `docker compose ps azurite` and `db-grants` |
| Every API call returns 401 | metadata fetch from `auth` failed, or `AUTH_METADATA_URL`/issuer/audience mismatched | check `docker compose logs api`; confirm `AUTH_REQUIRE_HTTPS_METADATA=false` in Compose and that auth is healthy |
| `issuer_sync` rows are `failed` | issuer-mock stopped, wrong `ISSUER_API_KEY`, or issuer returned an invalid payload | check `docker compose ps issuer-mock` and `docker compose logs api`; curl the issuer endpoint with the configured key; the API keeps serving the local feed meanwhile |
| Wallet has cards but no transactions after a fresh start | the first sync has not run yet, or it failed | wait for `ISSUER_SYNC_INTERVAL`, check `app.issuer_sync`, and re-run with a short `ISSUER_SYNC_INTERVAL=10s` if needed |
| Issuer returns 401 | `ISSUER_API_KEY` differs between `api` and `issuer-mock` | set one value for both in `.env` and recreate both containers |
| `web` is up but the page is blank | the built assets are missing or the module failed to load | check the browser console and `docker compose logs web`; rebuild with `docker compose up -d --build web` |
| `http://localhost:8080/auth/...` or `/api/...` returns 502 | nginx cannot resolve or reach `auth`/`api` | `docker compose ps auth api`; wait for both to be healthy, then `docker compose restart web` |
| Demo hint is stale or missing | `/config.js` was cached, or the container was not recreated after changing `.env` | reload with dev tools open, then `docker compose up -d --force-recreate web` |
| `docker compose build web` fails on `npm ci` | no network during the image build | connect to the registry, or run `cd web; npm ci` locally first to warm the npm cache |

## What is not here yet

The web UI is implemented for sign-in with OTP, the card wallet with artwork,
the card summary plus its paginated, date-grouped transaction list (pending
badges, `Load more`, retry on append failures, wallet scroll restoration), and
the profile page. CI builds and tests both halves on every push and pull request.
A real deployment and a real identity provider are not here yet (requirements
§10.6); the demo runs from Compose on one machine. The auth
issuer, the resource API, and the mock issuer are implemented with their own
databases, migrations, idempotent scripts, and tests. The mock SMS provider is a
stateless stand-in with its own contract tests. The API pulls transactions from
`issuer-mock` via `IssuerSyncService`; issuer failures degrade to the last good
local feed rather than failing requests. `auth` posts codes to `sms-mock`; an SMS
failure degrades login to a readable 503 rather than a 500.
