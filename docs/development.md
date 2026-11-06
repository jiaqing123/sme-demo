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
tools/Migrator                      one-shot schema applier (runs in its own container)
db/migrations/api/*.sql             generated, committed, idempotent schema scripts
compose.yaml                        SQL Server + migrator
docs/                               requirements, licensing, workshop
```

## First run

```powershell
Copy-Item .env.example .env      # then change the password
docker compose up -d mssql       # waits for the healthcheck
docker compose run --rm db-migrator
```

Expected output ends with `[migrator] applied 1 script(s).`

Verify:

```powershell
docker exec sme-demo-mssql-1 /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U sa -P "Local_Dev_Only_123!" -C `
  -Q "SELECT name FROM sys.tables"
```

You should see `cards`, `issuer_sync`, and `__EFMigrationsHistory`.

## Everyday commands

```powershell
dotnet tool restore                       # after cloning; installs dotnet-ef
dotnet build SmeDemo.slnx
dotnet test SmeDemo.slnx                  # migrator script-splitting tests
```

## Changing the schema

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

The script is idempotent, so re-running it is safe and should print the same
result. Commit the entity change, the migration, and the regenerated script in
one commit.

## Checking for drift

```powershell
dotnet tool run dotnet-ef migrations has-pending-model-changes `
  --project src/SmeDemo.Infrastructure `
  --startup-project src/SmeDemo.Infrastructure
```

It prints `No changes have been made to the model since the last migration` when
the model and migrations agree. Run it before every commit; CI runs it too.

## Resetting

```powershell
docker compose down -v
docker compose up -d mssql
docker compose run --rm db-migrator
```

`-v` removes the `mssql-data` volume, so this is a full reset. Do not use it on a
database you care about.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `create .env from .env.example` | `.env` missing | copy the example |
| Migrator exits 1 immediately | SQL Server still starting | it retries for `DB_WAIT_SECONDS`; check `docker compose logs mssql` |
| Login failed for user `sa` | password changed after the volume was initialised | `docker compose down -v`, or use the original password |
| `has-pending-model-changes` fails | entity edited without a migration | add the migration, do not hand-edit the snapshot |

## What is not here yet

The API, auth issuer, web UI, and mocks from `requirements.md` are not
implemented. This scaffold proves the two things that are expensive to change
later: central package pinning and the schema pipeline.
