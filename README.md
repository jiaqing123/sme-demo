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

The auth issuer, the resource API, and the web UI are implemented. What exists
today:

- The solution, central package management, and the licensing pins.
- The EF Core model for cards and issuer sync state.
- Versioned migrations, generated into committed, idempotent SQL scripts for
  both `sme_api` and `sme_auth`.
- One-shot `db-migrator` and `db-migrator-auth` containers plus `db-grants`,
  which gives each application a read/write-only login, wired into Compose with
  SQL Server.
- `web/` on port 8080: a React + TypeScript + Vite SPA built to static assets and
  served by nginx. nginx also reverse-proxies `/auth` and `/api`, so the browser
  only needs one origin. Sign-in (password then OTP), the card wallet with
  artwork fetched as blob URLs, the card summary and its paginated,
  date-grouped transactions (pending badges, `Load more`, wallet scroll
  restoration), and the profile page are implemented.
- `SmeDemo.Auth` on port 8081: password login, in-memory OTP challenges, RS256
  JWT issuance, OIDC discovery, JWKS, `/auth/userinfo`, health check, and a
  persisted signing key.
- `SmeDemo.Api` on port 8082: profile, cards, paginated transactions, card
  artwork, and demo cards/artwork seeded into SQL Server and Azurite Blob
  Storage.
- `SmeDemo.IssuerMock` on port 8084: a deterministic 40-transaction fixture,
  API-key auth, and the issuer health check. `IssuerSyncService` pulls the feed at
  start-up and every `ISSUER_SYNC_INTERVAL`; a failed issuer call is recorded in
  `app.issuer_sync` and never breaks the local read path.
- `SmeDemo.SmsMock` on port 8083: the HTTP SMS gateway used by `auth`. It
  validates `{ to, text }`, returns a text/plain provider id, and logs each
  accepted message in plain text so the demo OTP is visible in
  `docker compose logs sms-mock`. `auth` now posts codes over HTTP; a stopped
  gateway makes login return a readable `503` `UPSTREAM_UNAVAILABLE`.
- Unit and integration tests for the challenge rules, token claims, handlers,
  validators, HTTP contract, issuer fixture, SMS mock contract, and
  sync/SMS failure paths; 64 frontend tests for formatting, session, request
  handling, sign-in, the wallet and the transaction list.
- CI in `.github/workflows/ci.yml`: on every push to `main` and every pull
  request it builds and tests .NET (Release) plus the web app, and fails when
  either DbContext has a model change without a migration.

Not implemented yet: a real deployment and a real identity provider
(requirements §3 and §10.6; the demo runs locally from Compose).

## Quick start

```powershell
Copy-Item .env.example .env      # then change the passwords
docker compose up -d --build
```

Open `http://localhost:8080`. The demo login is `demo` / `Demo_Passw0rd!` with
OTP `123456`; the sign-in page also shows these demo hints while `DEMO_MODE` is
enabled. Port 8080 serves the SPA and proxies `/auth` and `/api`, so that is the
only URL a browser needs. `auth` is also published on 8081, `api` on 8082
(`/healthz`, `/api/v1/me`, `/api/v1/me/cards`), `sms-mock` on 8083, and
`issuer-mock` on 8084 for debugging. Every published port binds to `127.0.0.1`,
so the stack is reachable from this machine only: the demo password and OTP are
public by design. Details, the smoke tests, and the schema-change workflow are in
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
web/                                React + TypeScript + Vite SPA (nginx in Compose)
tools/SmsMock                       stand-in SMS gateway that logs each message
tools/IssuerMock                    deterministic card-issuer stand-in
tests/IssuerMock.Tests              issuer fixture/auth/ordering tests
tests/SmsMock.Tests                 SMS mock HTTP contract tests
tests/Migrator.Tests                migrator script tests
tests/SmeDemo.Application.Tests     handler and validator tests
tests/SmeDemo.Api.Tests             HTTP contract tests through WebApplicationFactory
tests/SmeDemo.Auth.Tests            challenge and token claim tests
tests/SmeDemo.Infrastructure.Tests  issuer sync service tests
tools/Migrator                      one-shot schema applier
db/migrations/api/                  generated idempotent API SQL scripts
db/migrations/auth/                 generated idempotent auth SQL scripts
db/bootstrap/                       read/write-only application login script
compose.yaml                        SQL Server, migrators, grants, Azurite, sms-mock, issuer-mock, auth, API, web
```

## Audience

Internal demo and discussion material for the product team, management, and
colleagues from other groups.
