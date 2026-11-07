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

The auth issuer and the resource API are implemented. What exists today:

- The solution, central package management, and the licensing pins.
- The EF Core model for cards and issuer sync state.
- Versioned migrations, generated into committed, idempotent SQL scripts for
  both `sme_api` and `sme_auth`.
- One-shot `db-migrator` and `db-migrator-auth` containers plus `db-grants`,
  which gives each application a read/write-only login, wired into Compose with
  SQL Server.
- `SmeDemo.Auth` on port 8081: password login, in-memory OTP challenges, RS256
  JWT issuance, OIDC discovery, JWKS, `/auth/userinfo`, health check, and a
  persisted signing key.
- `SmeDemo.Api` on port 8082: profile, cards, paginated transactions, card
  artwork, and seeded demo data in Azurite Table/Blob Storage.
- Unit and integration tests for the challenge rules, token claims, handlers,
  validators, and HTTP contract.

Not implemented yet: the web UI, the issuer/SMS mocks, and issuer sync.

## Quick start

```powershell
Copy-Item .env.example .env      # then change the passwords
docker compose up -d --build
```

The demo login is `demo` / `Demo_Passw0rd!` with OTP `123456`. `auth` is on
port 8081 and `api` on port 8082 (`/healthz`, `/api/v1/me`, `/api/v1/me/cards`).
Details, the smoke tests, and the schema-change workflow are in
[`docs/development.md`](docs/development.md).

## Repository layout

```
docs/requirements.md                System requirements and architecture
docs/development.md                 How to build, migrate, run auth, and reset
docs/licensing.md                   Third-party licensing notes for management review
docs/declarative-schema-workshop.md 45-minute walkthrough of state-based schema tooling
src/SmeDemo.Api                     resource API endpoints and composition root
src/SmeDemo.Application             handlers, DTOs, validators, ports
src/SmeDemo.Domain                  entities and invariants
src/SmeDemo.Infrastructure          EF Core model, migrations, Table/Blob adapters, seeders
src/SmeDemo.Auth                    independent JWT issuer, own DbContext and migration
tests/Migrator.Tests                migrator script tests
tests/SmeDemo.Application.Tests     handler and validator tests
tests/SmeDemo.Api.Tests             HTTP contract tests through WebApplicationFactory
tests/SmeDemo.Auth.Tests            challenge and token claim tests
tools/Migrator                      one-shot schema applier
db/migrations/api/                  generated idempotent API SQL scripts
db/migrations/auth/                 generated idempotent auth SQL scripts
db/bootstrap/                       read/write-only application login script
compose.yaml                        SQL Server, migrators, grants, Azurite, auth, API
```

## Audience

Internal demo and discussion material for the product team, management, and
colleagues from other groups.
