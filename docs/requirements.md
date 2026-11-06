# SME Demo — System Requirements & Architecture

- **Status:** Draft v0.2
- **Owner:** Architecture
- **Last updated:** 2026-11-06
- **Changes in v0.2:** ASP.NET Core implementation stack, three-tier layering,
  MediatR/AutoMapper, MSSQL + Cosmos DB + Azure Storage, separate JWT issuer
  container, mock issuer integration.

## 1. Purpose

A demonstration system for a single small/medium business (`sme`) that shows a
card-wallet experience: sign in, see your profile, see your cards, tap a card to
see its transactions.

The system runs entirely offline in Docker on one machine. It is small, but the
boundaries are real: the token issuer is a separate service from the API, tokens
are signed asymmetrically, and the card issuer is an external HTTP dependency
behind an adapter, not a direct database call.

## 2. Goals

| ID | Goal |
|----|------|
| G1 | One command starts the whole demo and it works with no internet access. |
| G2 | Authentication is architecturally separate from the resource API (separate service, separate signing key). |
| G3 | The UI is mobile-first and English-only, and looks like a consumer wallet app (Google Wallet as the reference). |
| G4 | Every demo shortcut is explicit and switchable, so nothing silently becomes production behaviour. |
| G5 | The layering is conventional and boring: thin endpoints, logic in the application layer, data and third-party access in the infrastructure layer. |
| G6 | Swapping the mock issuer or mock SMS provider for a real one is an adapter change, not a rewrite. |

## 3. Non-goals

- Admin roles, user management, registration, password reset, profile editing.
- Multiple organisations or tenants. One deployment serves exactly one
  organisation, configured as `ORG_ID=sme`.
- Real card data. No PAN, no CVV, no real issuer connection. All card and
  transaction data is seeded fiction, produced by a mock issuer.
- Real SMS delivery, real payments, statements, disputes, spend limits.
- Token refresh, revocation lists, device management.
- Production hardening: TLS, WAF, HA, horizontal scaling, full observability
  stack, audit trail.
- Polyglot persistence for its own sake. Each store below has a stated job; if
  it has no job, it does not ship.

## 4. Users and roles

| Role | Count | Description |
|------|-------|-------------|
| Cardholder | 1 | The only account. Signs in, views own profile, cards, and transactions. |

There are no administrators. All users are peers; the demo has exactly one.

## 5. Architecture

### 5.1 Runtime components

| Component | Responsibility | Persistent state | Port |
|-----------|----------------|------------------|------|
| `web` | React SPA, mobile-first wallet UI. Static assets served by nginx. | none | 8080 |
| `auth` | Credential check, OTP challenge, JWT issuance, OIDC-style discovery and JWKS. | MSSQL `sme_auth`, signing keys volume | 8081 |
| `api` | Resource server. Verifies JWTs, serves profile, cards, transactions, card art. | MSSQL `sme_api`, Cosmos DB, Blob | 8082 |
| `issuer-mock` | Stand-in card issuer. Serves card transactions over HTTP. | none (in-memory fixture) | 8084 |
| `sms-mock` | Stand-in SMS provider. Accepts a send request and logs it. | none | 8083 |
| `mssql` | SQL Server 2022. Hosts `sme_auth` and `sme_api`. | `mssql-data` volume | internal 1433 |
| `cosmos` | Azure Cosmos DB emulator. Stores the transaction feed. | `cosmos-data` volume | internal 8081 |
| `azurite` | Azure Storage emulator. Stores card artwork. | `azurite-data` volume | internal 10000 |

Only `web`, `auth`, `api`, `sms-mock`, and `issuer-mock` publish host ports.
Database and storage containers are reachable only on the Compose network.

### 5.2 Runtime view

```mermaid
flowchart LR
    B[Browser] -->|static assets| W[web :8080]
    B -->|"POST /auth/*"| A[auth :8081]
    B -->|"GET /api/v1/* + Bearer JWT"| P[api :8082]
    A -->|"users, password hashes"| M[(mssql)]
    P -->|"cards, sync state"| M
    P -->|"transactions"| C[(cosmos)]
    P -->|"card art"| Z[(azurite)]
    P -->|"GET transactions"| I[issuer-mock :8084]
    A -->|"JWKS"| P
    A -->|"POST /messages"| S[sms-mock :8083]
```

The browser never talks to a database, the issuer mock, or the SMS mock. `api`
fetches `auth`'s public keys over the Compose network and never holds a signing
key.

### 5.3 Trust boundaries

- `auth` is the only component that can mint a token.
- `api` trusts a token only after verifying its signature against `auth`'s
  JWKS, plus `iss`, `aud`, and `exp`.
- `org` in the token is checked against the configured `ORG_ID`; it is not on
  its own a permission to see data. Every query is additionally scoped by the
  token's `sub`.
- The issuer mock is untrusted input. Payloads are validated before storage;
  its failures degrade the feed, they do not corrupt the ledger.

### 5.4 Implementation stack

| Concern | Choice |
|---------|--------|
| Runtime | .NET 10 (LTS), C# 14 |
| API host | ASP.NET Core minimal APIs |
| Endpoint orchestration | MediatR (community/free version pinned — see §13.2) |
| Object mapping | AutoMapper (free version pinned — see §13.2) |
| Validation | `System.ComponentModel.DataAnnotations` + a MediatR pipeline behaviour |
| Persistence (relational) | EF Core 10 + `Microsoft.EntityFrameworkCore.SqlServer` |
| Persistence (documents) | `Microsoft.Azure.Cosmos` |
| Blob storage | `Azure.Storage.Blobs` |
| Outbound HTTP | `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience` |
| Authentication | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| Token creation | `Microsoft.IdentityModel.JsonWebTokens` |
| Password hashing | `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (PBKDF2) |
| OpenAPI | built-in `Microsoft.AspNetCore.OpenApi` |
| Errors | built-in `ProblemDetails` + `IExceptionHandler` |
| Health checks | `Microsoft.Extensions.Diagnostics.HealthChecks` |
| Logging | built-in `Microsoft.Extensions.Logging`, JSON console |

Deliberately not used, to honour "prefer Microsoft packages": FluentValidation,
Serilog, Swashbuckle, Dapper, Mapster, MassTransit.

### 5.5 Layering rules

Three tiers plus a domain project, all in one solution:

```
src/
  SmeDemo.Api/              presentation: endpoints, auth wiring, composition root
  SmeDemo.Application/      use cases: MediatR handlers, DTOs, profiles, port interfaces
  SmeDemo.Domain/           entities, value objects, domain errors
  SmeDemo.Infrastructure/   adapters: EF Core, Cosmos, Blob, issuer HTTP client
  SmeDemo.Auth/             independent JWT issuer service, own project and Dockerfile
tests/
  SmeDemo.Application.Tests/
  SmeDemo.Api.Tests/        integration through WebApplicationFactory
```

The rules that make "thin endpoints" mean something:

| Rule | Enforcement |
|------|-------------|
| An endpoint may only bind input, call `ISender.Send`, and map the result to HTTP. | Code review; the `Api` project has no reference to `Infrastructure`. |
| No `DbContext`, `CosmosClient`, `BlobClient`, or `HttpClient` in `Api`. | Project references make it impossible to compile. |
| No business rules in handlers that belong to entities. | Domain project owns invariants; handlers orchestrate. |
| Every third-party dependency is behind an interface declared in `Application`. | Adapters live in `Infrastructure` and are registered in the composition root. |
| Cross-cutting concerns are pipeline behaviours, not repeated code. | Logging, validation, and timing behaviours in `Application`. |

Typical request path: endpoint → `ISender.Send(GetCardTransactionsQuery)` →
validation behaviour → handler → `ITransactionStore` / `ICardRepository` →
DTO mapped by AutoMapper → `Results.Ok` or a `ProblemDetails` result.

### 5.6 Container and resource footprint

Eight containers. Approximate steady-state memory:

| Container | Memory | Notes |
|-----------|--------|-------|
| `mssql` | ~2 GB | SQL Server 2022 Developer image. |
| `cosmos` | 2–4 GB | Emulator; 4 GB recommended, slow first start. |
| `azurite` | ~150 MB | |
| `web` | ~50 MB | nginx + static assets. |
| `auth` | ~200 MB | |
| `api` | ~250 MB | |
| `issuer-mock` | ~150 MB | |
| `sms-mock` | ~150 MB | |
| **Total** | **~6–7 GB** | Docker Desktop memory should be raised to ≥ 10 GB. |

This is the main cost of the "all stores in Docker" requirement, and it is
called out in §13.

## 6. Authentication

### 6.1 Flow

```mermaid
sequenceDiagram
    participant B as Browser
    participant A as auth
    participant S as sms-mock
    B->>A: POST /auth/login {username, password}
    A->>A: verify password hash
    A->>A: create challenge (5 min TTL)
    A->>S: POST /messages {to, text}
    S-->>A: 202 Accepted
    A-->>B: 200 {challenge_id, expires_in, resend_in}
    B->>A: POST /auth/challenges/{id}/verify {code}
    A->>A: check code, attempts, expiry
    A-->>B: 200 {access_token, token_type, expires_in}
    B->>P: GET /api/v1/me (Authorization: Bearer ...)
```

### 6.2 Endpoints (auth)

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/auth/login` | Verify username/password, create MFA challenge, trigger SMS. |
| POST | `/auth/challenges/{challenge_id}/verify` | Verify OTP, return access token. |
| POST | `/auth/challenges/{challenge_id}/resend` | Re-send OTP, subject to cooldown. |
| GET | `/.well-known/openid-configuration` | Issuer metadata (`issuer`, `jwks_uri`). |
| GET | `/.well-known/jwks.json` | Public signing keys. |
| GET | `/healthz` | Liveness for Compose. |

The discovery document exists so the API can use the stock `JwtBearer` handler
with `MetadataAddress`, instead of hand-rolled validation code.

### 6.3 Challenge rules

| Rule | Value |
|------|-------|
| Challenge TTL | 5 minutes |
| Max verification attempts | 3, then the challenge is destroyed |
| Resend cooldown | 60 seconds |
| Challenge reuse | Single use; a successful verify invalidates it |
| Challenge storage | In-memory in `auth` (single instance, demo only) |

Codes and challenges are never logged in plaintext by the API or the UI.

### 6.4 Demo mode

`DEMO_MODE=true` makes the following true:

- The only accepted username, password, and phone number come from environment
  variables (`DEMO_USERNAME`, `DEMO_PASSWORD`, `DEMO_PHONE`).
- The OTP is the fixed value `DEMO_OTP` (default `123456`) instead of a random
  code.
- The login screen shows a clearly labelled demo hint with the credentials.

With `DEMO_MODE=false`, the fixed OTP path is refused at startup and a real
`ISmsProvider` must be configured. This prevents the fixed code from ever
becoming a back door.

### 6.5 SMS provider seam

`auth` depends on:

```
ISmsProvider.Send(phoneNumber, message, cancellationToken) -> providerMessageId
```

The only demo implementation posts to `SMS_PROVIDER_URL` (`sms-mock`). Replacing
it with Twilio, SNS, or an internal gateway means one adapter plus one
environment variable.

### 6.6 Token specification

- Format: JWT, **RS256**, 2048-bit RSA, `kid` in the header.
- Issuer metadata and JWKS are published by `auth`; keys are generated on first
  start and persisted in the `auth-keys` volume.

| Claim | Example | Notes |
|-------|---------|-------|
| `iss` | `http://auth:8081` | Must equal `JWT_ISSUER`. |
| `aud` | `sme-demo-api` | Must equal `JWT_AUDIENCE`. |
| `sub` | `7f3c...` | User ID. The only identifier used for data lookups. |
| `org` | `sme` | Organisation code. Must equal the API's `ORG_ID`. |
| `name` | `Alex Morgan` | Display convenience only. |
| `iat` / `exp` | — | TTL 30 minutes (`JWT_TTL`). |
| `jti` | UUID | Unique token ID, logged for traceability. |

Deliberate omissions:

- **No phone number in the token.** It is PII, it would end up in logs and
  browser storage, and the API can read it from the database anyway.
- **No roles or permissions.** With one peer-level user there is nothing to
  express; adding a role model now would be speculative.

Accepted limitations: no refresh token, no revocation.

### 6.7 Issuer implementation notes

- `auth` is a separate project (`SmeDemo.Auth`) with its own Dockerfile and its
  own database (`sme_auth`) on the shared MSSQL instance. It shares no domain
  code with `api`.
- Password hashes use `PasswordHasher<User>` (PBKDF2-HMAC-SHA256, per-user
  salt). Even the single demo account is stored hashed.
- The signing key is generated on first start, written to `/keys` on the
  `auth-keys` volume, and never committed.
- `api` configures `JwtBearer` with `MetadataAddress =
  http://auth:8081/.well-known/openid-configuration` and
  `RequireHttpsMetadata = false`. That flag is development-only and must be
  tied to configuration, not hard-coded.

## 7. Resource API

Base path `/api/v1`. JSON only. `Authorization: Bearer <jwt>` on every request
except `/healthz`.

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/v1/me` | Profile: id, name, phone, org. |
| GET | `/api/v1/me/cards` | All cards for the caller, in display order. |
| GET | `/api/v1/me/cards/{card_id}/transactions` | Transactions for one card, newest first. |
| GET | `/api/v1/cards/{card_id}/art` | Card artwork, streamed from blob storage. |
| GET | `/healthz` | Liveness for Compose. |
| GET | `/openapi/v1.json` | OpenAPI document (Development only). |

### 7.1 Responses

- `GET /me` → `{ "id", "name", "phone", "org" }`.
- `GET /me/cards` → `{ "items": [ { "id", "brand", "last4", "expiry_month",
  "expiry_year", "status", "currency", "display_order", "art_url" } ] }`.
- Transactions → `{ "items": [ { "id", "card_id", "amount_minor", "currency",
  "direction", "merchant", "status", "occurred_at" } ], "next_cursor": "..." }`.

The UI never constructs storage URLs; `art_url` always points back at the API,
so blob credentials never reach the browser.

### 7.2 Errors

```json
{ "error": { "code": "TOKEN_EXPIRED", "message": "Access token has expired." } }
```

| Code | HTTP | When |
|------|------|------|
| `VALIDATION_ERROR` | 400 | Malformed body or query. |
| `UNAUTHENTICATED` | 401 | Missing or unparseable bearer token. |
| `TOKEN_EXPIRED` | 401 | `exp` in the past. |
| `INVALID_TOKEN` | 401 | Signature, `iss`, or `aud` mismatch. |
| `FORBIDDEN` | 403 | Token valid but org mismatch or resource not owned. |
| `NOT_FOUND` | 404 | Unknown card ID. |
| `UPSTREAM_UNAVAILABLE` | 503 | Issuer or storage unavailable after retries. |
| `RATE_LIMITED` | 429 | Too many OTP attempts or sends. |
| `INTERNAL` | 500 | Unhandled failure. |

### 7.3 Pagination

Cursor-based, `limit` default 20, maximum 100. The Cosmos continuation token is
wrapped as an opaque base64url string, so the contract does not leak the store's
cursor format. `next_cursor` is `null` on the last page. No total counts.

## 8. Data model

### 8.1 Store ownership

| Store | Owns | Why |
|-------|------|-----|
| MSSQL `sme_auth` | users, password hashes | Relational, transactional, written only by `auth`. |
| MSSQL `sme_api` | cards, issuer sync state | Small, relational, joined on demand. |
| Cosmos DB `transactions` | transaction documents | Append-only, issuer-shaped, queried per card. |
| Blob `card-art` | card artwork images | Binary assets, not queryable. |

There is no dual write. Each fact has exactly one home, and only `api` reads
more than one store.

### 8.2 `auth.users` (MSSQL `sme_auth`)

| Column | Type | Notes |
|--------|------|-------|
| `id` | uniqueidentifier, PK | Becomes `sub`. |
| `org_id` | nvarchar(32) | `sme`. |
| `username` | nvarchar(128), unique | Demo value from env. |
| `password_hash` | nvarchar(256) | PBKDF2, never plaintext. |
| `name` | nvarchar(128) | Display name. |
| `phone` | nvarchar(32) | E.164. MFA destination. |
| `status` | nvarchar(16) | `active` / `disabled`. |
| `created_at` | datetime2 | UTC. |

### 8.3 `app.cards` (MSSQL `sme_api`)

| Column | Type | Notes |
|--------|------|-------|
| `id` | uniqueidentifier, PK | |
| `user_id` | uniqueidentifier | Logical reference to `auth.users.id`; no cross-database FK. |
| `org_id` | nvarchar(32) | `sme`. |
| `brand` | nvarchar(32) | `visa`, `mastercard`. |
| `last4` | char(4) | **Only the last four digits are ever stored.** |
| `expiry_month` | tinyint | 1–12. |
| `expiry_year` | smallint | Four digits. |
| `status` | nvarchar(16) | `active`, `frozen`, `expired`. |
| `currency` | char(3) | ISO 4217, `AUD`. |
| `display_order` | smallint | Wallet ordering. |
| `art_blob_name` | nvarchar(128) | Blob name for the card artwork. |

### 8.4 `app.issuer_sync` (MSSQL `sme_api`)

| Column | Type | Notes |
|--------|------|-------|
| `card_id` | uniqueidentifier, PK | |
| `last_synced_at` | datetime2 | UTC. |
| `last_status` | nvarchar(16) | `ok` / `failed`. |
| `last_error` | nvarchar(512), null | Redacted message. |

This is the only table that records integration state, and it is what makes the
issuer dependency visible rather than implicit.

### 8.5 `transactions` (Cosmos DB)

Partition key `/cardId`. Document body:

```json
{
  "id": "txn_01J...",
  "cardId": "8b1f...",
  "orgId": "sme",
  "amountMinor": 1240,
  "currency": "AUD",
  "direction": "debit",
  "merchant": "Southbank Coffee",
  "status": "posted",
  "occurredAt": "2026-11-02T08:14:00Z",
  "postedAt": "2026-11-02T08:15:00Z",
  "issuerPayload": { },
  "ingestedAt": "2026-11-02T09:00:00Z"
}
```

A composite index on `(cardId ASC, occurredAt DESC, id ASC)` backs the feed
query. Documents are upserted by `id`, so re-syncing the same page is idempotent.

`issuerPayload` keeps the raw response for diagnostics. It is never returned by
the API.

### 8.6 Transaction store abstraction

`Application` declares `ITransactionStore` with one read method (by card, cursor,
limit) and one write method (upsert batch). Two adapters exist:

- `CosmosTransactionStore` — default, used by `docker compose up`.
- `SqlTransactionStore` — contingency for machines where the emulator will not
  run; writes to an `app.transactions` table.

Selected by `TRANSACTIONS_STORE=cosmos|mssql`. This is demo insurance, not a
supported dual-write design: exactly one adapter is active per deployment, and
the contingency adapter will be deleted once the emulator path is proven stable.

### 8.7 Seed and sync

1. `auth` seeds one user (from env values) if the table is empty.
2. `api` seeds three cards and their artwork (uploaded to `card-art`) if empty.
3. `IssuerSyncService` (a hosted background service) calls the issuer for every
   card at startup and every 5 minutes, upserting results into the transaction
   store.
4. The UI therefore reads a warm, local feed and never waits on the issuer.

The mock issuer serves deterministic fixture data, so a reset always produces
the same demo.

## 9. Front-end

### 9.1 Stack

React 18 + TypeScript + Vite + React Router + Tailwind CSS, built to static
assets and served by nginx. This matches the production stack, so the demo UI is
disposable but not alien.

### 9.2 Routes

| Route | Screen | Notes |
|-------|--------|-------|
| `/login` | Sign-in | Username, password, then OTP step. Demo hint visible in demo mode. |
| `/wallet` | Card wallet | Grid of card tiles. Default route after sign-in. |
| `/wallet/cards/:cardId` | Card detail | Card summary plus its transactions. |
| `/profile` | Profile | Name, phone, organisation from `/me`. |

### 9.3 Wallet experience

- Cards render as rounded rectangles at roughly 1.586:1, in a responsive grid:
  one column at 360 px, two at 640 px, three at 1024 px, capped at a comfortable
  max width.
- Each tile shows brand, masked number (`•••• 4242`), expiry, and status.
- Tapping a tile navigates to that card's transactions; back returns to the
  wallet without losing scroll position.
- Visual tone: brand-coloured gradients, generous spacing, large tap targets
  (minimum 44 px), no dense tables.

### 9.4 Transactions screen

- Card summary at the top, then transactions newest first, grouped by date.
- Each row: merchant, time, signed amount (`−A$12.40` / `+A$5.00`), and a
  `Pending` badge when applicable.
- "Load more" using `next_cursor`. Empty state: "No transactions yet."

### 9.5 States

Every screen handles: loading (skeleton, not a spinner-only blank), empty,
request failure with retry, and expired session.

### 9.6 Session handling

- The token is held in `sessionStorage` and attached by a single fetch wrapper.
- Any `401` clears the token and redirects to `/login` with a short message.
- No silent refresh; expiry after 30 minutes returns the user to sign-in.

### 9.7 Localisation

English only, `en-AU` formatting: `A$` currency symbol, `DD MMM YYYY` dates.

## 10. Docker and runtime

### 10.1 Compose services

| Service | Image | Published | Healthcheck |
|---------|-------|-----------|-------------|
| `web` | multi-stage Node build → nginx | 8080 | `GET /` |
| `auth` | built from `src/SmeDemo.Auth` | 8081 | `GET /healthz` |
| `api` | built from `src/SmeDemo.Api` | 8082 | `GET /healthz` |
| `sms-mock` | built from `tools/SmsMock` | 8083 | `GET /healthz` |
| `issuer-mock` | built from `tools/IssuerMock` | 8084 | `GET /healthz` |
| `mssql` | `mcr.microsoft.com/mssql/server:2022-latest` | none | `sqlcmd SELECT 1` |
| `cosmos` | `mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator` | none | gateway probe |
| `azurite` | `mcr.microsoft.com/azure-storage/azurite` | none | blob probe |

Startup ordering: `mssql`, `cosmos`, `azurite` healthy → `auth` → `api`;
`api` also waits for `issuer-mock` and `sms-mock`.

### 10.2 Compose profiles

- Default (`docker compose up --build`): all eight containers.
- Lite (`docker compose -f compose.yaml -f compose.lite.yaml up --build`):
  drops `cosmos` and sets `TRANSACTIONS_STORE=mssql`.

The lite profile exists so a demo can still run on a smaller laptop or on a
machine where the Cosmos emulator misbehaves.

### 10.3 Configuration

| Variable | Used by | Default |
|----------|---------|---------|
| `ORG_ID` | auth, api | `sme` |
| `DEMO_MODE` | auth, web | `true` |
| `DEMO_USERNAME` / `DEMO_PASSWORD` / `DEMO_PHONE` | auth | demo values |
| `DEMO_OTP` | auth | `123456` |
| `JWT_ISSUER` / `JWT_AUDIENCE` / `JWT_TTL` | auth, api | `http://auth:8081`, `sme-demo-api`, `30m` |
| `JWKS_URL` / `AUTH_METADATA_URL` | api | `http://auth:8081/.well-known/...` |
| `SMS_PROVIDER_URL` | auth | `http://sms-mock:8083` |
| `ISSUER_BASE_URL` / `ISSUER_API_KEY` | api | `http://issuer-mock:8084`, fake key |
| `TRANSACTIONS_STORE` | api | `cosmos` |
| `COSMOS_CONNECTION_STRING` | api | emulator DSN (`https://cosmos:8081`) |
| `COSMOS_EMULATOR` | api | `true` — enables the dev-only certificate bypass |
| `AZURE_STORAGE_CONNECTION_STRING` | api | `UseDevelopmentStorage=true` |
| `DATABASE_URL` (auth/api) | auth, api | Compose-internal DSNs |
| `CORS_ORIGIN` | auth, api | `http://localhost:8080` |

Secrets live in `.env` (git-ignored, with `.env.example` committed). No secret
is baked into an image.

### 10.4 Start, seed, reset

- `docker compose up --build` builds and starts everything.
- Seed data is applied idempotently at start-up (one user, three cards, about
  40 transactions fetched from the mock issuer).
- `docker compose down -v && docker compose up` returns the demo to its initial
  state.

### 10.5 Offline behaviour

Images are pulled and NuGet/npm dependencies restored once during
`docker compose build`, when a network is available. After that the demo runs
with no outbound traffic: no external SMS, no CDN assets, no remote fonts.

## 11. Security posture

Implemented:

- Passwords hashed with PBKDF2 (ASP.NET Core Identity hasher), even for the one
  demo account.
- RS256 tokens, 30-minute TTL, signature and `iss`/`aud`/`exp` all validated.
- The fixed OTP is unreachable unless `DEMO_MODE=true`.
- CORS allowlist limited to the web origin.
- No PAN, CVV, or PIN anywhere in code, seed data, or logs.
- Card art served only through the API; storage credentials stay server-side.
- Phone numbers and tokens redacted from logs.

Accepted for the demo, listed so nobody mistakes them for production:

- Plain HTTP on localhost, no TLS.
- Token in `sessionStorage` rather than an httpOnly cookie.
- In-memory challenge store; an `auth` restart drops pending challenges.
- No revocation, no refresh token.
- Rate limiting is per-challenge, not per-IP.
- Cosmos emulator certificate validation is disabled when `COSMOS_EMULATOR=true`.
  This flag must be false anywhere else, and it is read from configuration, not
  defaulted in code.

## 12. Verification and acceptance

The demo is done when all of these hold on a clean machine:

1. `docker compose up --build` brings all eight services healthy with no manual
   steps.
2. `http://localhost:8080` loads the sign-in screen on a 360 px viewport without
   horizontal scrolling.
3. Wrong password is rejected with a readable message; three wrong OTPs destroy
   the challenge.
4. Correct credentials plus the demo OTP land on the wallet with three cards,
   each showing its artwork.
5. Tapping a card shows that card's transactions, newest first, with correct
   signed amounts and currency.
6. "Load more" paginates without duplicates.
7. Refreshing the page keeps the session; expiry or cleared storage returns to
   sign-in.
8. `/profile` shows the name, phone, and organisation from the API, not from the
   token.
9. Signing out clears the session and returns to `/login`.
10. With the network disconnected after build, the demo still passes steps 2–9.
11. Stopping `issuer-mock` leaves the wallet usable and logs a handled upstream
    failure; it does not return 500 to the browser.
12. `docker compose -f compose.yaml -f compose.lite.yaml up --build` produces the
    same demo without the Cosmos container.

## 13. Risks and decisions

### 13.1 The Cosmos emulator is the least reliable component

It is x64-only on the classic image, needs a self-signed certificate bypass,
takes minutes to start, reserves 2–4 GB, and its advertised gateway host can
fight with a container hostname. Mitigations, in order:

1. Do not publish its ports; reach it as `https://cosmos:8081` internally.
2. Bypass certificate validation only under `COSMOS_EMULATOR=true`.
3. Fall back to the lite profile (`TRANSACTIONS_STORE=mssql`) for the demo
   itself; the Cosmos path remains the reference implementation.
4. On ARM64 hardware, use the `vnext-preview` emulator image, which supports
   more platforms but only gateway mode with a subset of features.

### 13.2 MediatR and AutoMapper are no longer free for every organisation

Both moved to a dual RPL-1.5 / commercial model under Lucky Penny Software.
MediatR 13+ and AutoMapper 15+ require either a commercial licence or the free
Community edition, which is limited to organisations under US$5M gross annual
revenue (and, for MediatR, under US$10M in outside capital) and requires
registering a licence key.

For a demo, the cheapest and least risky option is to pin the last
permissively-licensed versions (MediatR 12.x, AutoMapper 14.x or earlier) and
record that pin in `Directory.Packages.props`. If the organisation prefers
current versions, budget the Community licence registration instead. This must
be an explicit decision before the first commit (see §14.1).

### 13.3 Eight containers is a lot for a laptop

Total footprint is roughly 6–7 GB. If the demo machine has 8 GB, the lite
profile is the realistic target. Do not promise "it runs anywhere" without
measuring on the actual demo hardware.

### 13.4 Mock issuer determinism

The mock returns fixed fixtures keyed by card ID. If the fixtures ever change,
the acceptance screenshots change too. Treat the fixture file as part of the
demo contract.

## 14. Open questions

1. **Licensing:** pin the free MediatR 12.x / AutoMapper 14.x, or upgrade and
   register Community keys? Default in this document: pin.
2. **Demo hardware:** which machine and Docker Desktop memory allocation will be
   used? This decides whether Cosmos is on the critical path.
3. **Real IdP:** is Auth0, Entra, or Cognito expected later? If so, `auth` will
   be replaced rather than extended — which is fine, but it changes how much
   credential logic is worth writing.
4. **SMS provider:** which one, so the adapter can be written against its shape
   when the time comes.
5. **Projector or phone:** which layout is rehearsed as the primary target.
