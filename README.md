# SME Demo

A demo system for small and medium-sized businesses, built as three layers:

- **Front-end UI** — the interface people actually use.
- **API service** — business logic, authentication, and integration.
- **Data storage** — persistence for business data.

## Stack

- **Front end:** React + TypeScript (demo UI), English, mobile-first.
- **API:** ASP.NET Core, three-tier with MediatR and AutoMapper.
- **Auth:** a separate ASP.NET Core JWT issuer service.
- **Data:** SQL Server, plus Azure Table and Blob Storage via Azurite — all in
  Docker, offline.

## Status

Skeleton stage. What exists today:

- The solution, central package management, and the licensing pins.
- The EF Core model for cards and issuer sync state.
- Versioned migrations, generated into a committed, idempotent SQL script.
- A one-shot `db-migrator` container that applies the script, wired into
  Compose with SQL Server.

Not implemented yet: the API, the auth issuer, the web UI, and the mocks.

## Quick start

```powershell
Copy-Item .env.example .env      # then change the password
docker compose up -d mssql
docker compose run --rm db-migrator
```

Details and the schema-change workflow are in
[`docs/development.md`](docs/development.md).

## Repository layout

```
docs/requirements.md                System requirements and architecture
docs/development.md                 How to build, migrate, and reset
docs/licensing.md                   Third-party licensing notes for management review
docs/declarative-schema-workshop.md 45-minute walkthrough of state-based schema tooling
src/SmeDemo.Domain                  entities and invariants
src/SmeDemo.Infrastructure          EF Core model and migrations
tools/Migrator                      one-shot schema applier
db/migrations/api/                  generated idempotent SQL scripts
compose.yaml                        SQL Server and the migrator
```

## Audience

Internal demo and discussion material for the product team, management, and
colleagues from other groups.
