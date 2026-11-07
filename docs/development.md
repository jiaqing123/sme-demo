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
tools/Migrator                     one-shot schema applier (runs in its own container)
db/migrations/api/*.sql            generated, committed, idempotent API schema scripts
db/migrations/auth/*.sql           generated, committed, idempotent auth schema scripts
db/bootstrap/app-logins.sql        creates the read/write-only application logins
tests/SmeDemo.Application.Tests    handler and validator unit tests
tests/SmeDemo.Api.Tests            integration tests through WebApplicationFactory
tests/SmeDemo.Auth.Tests           challenge-rule and token-claim tests
compose.yaml                       SQL Server, migrators, grants, Azurite, auth, API
docs/                              requirements, licensing, workshop
```

## First run

```powershell
Copy-Item .env.example .env      # then change the passwords
docker compose up -d --build
```

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

### Storage and seeding

`azurite` provides Table Storage (transactions) and Blob Storage (`card-art`) on
the internal network only. The API seeds three cards, their SVG artwork, and
about 40 transactions at start-up; every write is an idempotent upsert, so a
restart or a second run does not duplicate data. Set `SEED_ENABLED=false` to skip
seeding (the API tests do).

Inside Compose the connection string must name the endpoints explicitly:

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;...;BlobEndpoint=http://azurite:10000/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;
```

`UseDevelopmentStorage=true` is only for running the API on the host against a
local Azurite.

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

## Everyday commands

```powershell
dotnet tool restore                       # after cloning; installs dotnet-ef
dotnet build SmeDemo.slnx
dotnet test SmeDemo.slnx                  # migrator, auth, application, and API tests
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
the model and migrations agree. Run it before every commit; a CI check is planned.

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
| Auth cannot write `/keys` | key volume not writable by the app user | `docker compose down -v` to recreate the volume |
| `api` unhealthy or restarts | Azurite/Table/Blob not ready, or SQL login lacks rights | check `docker compose logs api`: the seeder retries 20 times, then stops the host; check `docker compose ps azurite` and `db-grants` |
| Every API call returns 401 | metadata fetch from `auth` failed, or `AUTH_METADATA_URL`/issuer/audience mismatched | check `docker compose logs api`; confirm `AUTH_REQUIRE_HTTPS_METADATA=false` in Compose and that auth is healthy |

## What is not here yet

The web UI, the issuer/SMS mocks, and the issuer sync service from
`requirements.md` are not implemented. The auth issuer and the resource API are
implemented with their own databases, migrations, idempotent scripts, and tests;
transactions are seeded locally rather than pulled from an issuer.
