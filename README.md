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

The auth issuer is implemented. What exists today:

- The solution, central package management, and the licensing pins.
- The EF Core model for cards and issuer sync state.
- Versioned migrations, generated into committed, idempotent SQL scripts for
  both `sme_api` and `sme_auth`.
- One-shot `db-migrator` and `db-migrator-auth` containers, wired into Compose
  with SQL Server.
- `SmeDemo.Auth` on port 8081: password login, in-memory OTP challenges, RS256
  JWT issuance, OIDC discovery, JWKS, health check, and a persisted signing key.
- Unit tests for the challenge rules and token claims.

Not implemented yet: the resource API, the web UI, and the issuer/SMS mocks.

## Quick start

```powershell
Copy-Item .env.example .env      # then change the password
docker compose up -d --build mssql db-migrator db-migrator-auth auth
```

The demo login is `demo` / `Demo_Passw0rd!` with OTP `123456`. Details, the
auth smoke test, and the schema-change workflow are in
[`docs/development.md`](docs/development.md).

## Repository layout

```
docs/requirements.md                System requirements and architecture
docs/development.md                 How to build, migrate, run auth, and reset
docs/licensing.md                   Third-party licensing notes for management review
docs/declarative-schema-workshop.md 45-minute walkthrough of state-based schema tooling
src/SmeDemo.Domain                  entities and invariants
src/SmeDemo.Infrastructure          EF Core model and migrations
src/SmeDemo.Auth                    independent JWT issuer, own DbContext and migration
tests/Migrator.Tests                migrator script tests
tests/SmeDemo.Auth.Tests            challenge and token claim tests
tools/Migrator                      one-shot schema applier
db/migrations/api/                  generated idempotent API SQL scripts
db/migrations/auth/                 generated idempotent auth SQL scripts
compose.yaml                        SQL Server, both migrators, and auth
```

## Audience

Internal demo and discussion material for the product team, management, and
colleagues from other groups.
