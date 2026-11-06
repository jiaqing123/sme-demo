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
src/SmeDemo.Domain                  entities and invariants
src/SmeDemo.Infrastructure          EF Core model, configurations, migrations
src/SmeDemo.Auth                    independent JWT issuer (own database and keys)
tools/Migrator                      one-shot schema applier (runs in its own container)
db/migrations/api/*.sql             generated, committed, idempotent API schema scripts
db/migrations/auth/*.sql            generated, committed, idempotent auth schema scripts
tests/SmeDemo.Auth.Tests            challenge-rule and token-claim tests
compose.yaml                        SQL Server, both migrators, and auth
docs/                               requirements, licensing, workshop
```

## First run

```powershell
Copy-Item .env.example .env      # then change the password
docker compose up -d --build mssql db-migrator db-migrator-auth auth
```

The two migrator containers run once and exit. `auth` starts only after
`db-migrator-auth` exits successfully. Check it with:

```powershell
docker inspect --format '{{.State.Health.Status}}' sme-demo-auth-1
```

Expected output: `healthy`.

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

The single user is seeded at start-up when the table is empty. With
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

## Everyday commands

```powershell
dotnet tool restore                       # after cloning; installs dotnet-ef
dotnet build SmeDemo.slnx
dotnet test SmeDemo.slnx                  # migrator, challenge, and token tests
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
docker compose up -d --build mssql db-migrator db-migrator-auth auth
```

`-v` removes `mssql-data` and `auth-keys`, so this is a full reset. Do not use it
on a database you care about.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `create .env from .env.example` | `.env` missing | copy the example |
| Migrator exits 1 immediately | SQL Server still starting | it retries for `DB_WAIT_SECONDS`; check `docker compose logs mssql` |
| Login failed for user `sa` | password changed after the volume was initialised | `docker compose down -v`, or use the original password |
| `has-pending-model-changes` fails | entity edited without a migration | add the migration, do not hand-edit the snapshot |
| Auth exits at start-up | `DEMO_MODE=false` with `DEMO_OTP` set, or no `SMS_PROVIDER_URL` | remove `DEMO_OTP`, set the SMS URL, or enable demo mode |
| Auth cannot write `/keys` | key volume not writable by the app user | `docker compose down -v` to recreate the volume |

## What is not here yet

The resource API, web UI, and mocks from `requirements.md` are not implemented.
The auth issuer is implemented with its own database, migration, idempotent
script, key volume, and unit tests.
