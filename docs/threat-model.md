# Threat Model — SME Wallet Reference Architecture

- **Status:** Draft v0.1
- **Owner:** Architecture
- **Last updated:** 2026-11-11
- **Scope:** this repository: `web`, `auth`, `api`, `issuer-mock`, `sms-mock`,
  Compose, CI, and the container host.
- **Normative:** yes. `docs/requirements.md` §11 makes this document a release
  gate for any production adaptation. It is **not** a compliance certification;
  PCI DSS, privacy, and local regulatory obligations need separate legal and
  security review.

## 1. Why this exists

The company is in financial services and is a plausible target for motivated
attackers: credential stuffing, account takeover, card-data theft, ransomware,
and supply-chain compromise. This repository is the reference implementation for
future systems, so its defaults must be those we would accept in production.

Two rules follow:

1. **Security is a release gate, not a hardening pass.** A feature is not done
   until its abuse cases are tested and its trust boundaries are reviewed.
2. **Demo shortcuts must stay visible.** Every shortcut is isolated behind
   `DEMO_MODE`, Compose, or localhost, listed in §7, and never becomes a silent
   production default.

## 2. Assets

| Asset | Where it lives | Impact if compromised |
|---|---|---|
| Customer password hashes | `auth.users` | Credential stuffing, account takeover |
| OTP challenges | `auth` in-memory store | Login bypass |
| JWT signing key | `auth` persisted key store | Forged tokens for any user |
| Access tokens | browser `sessionStorage`, request headers, logs | Impersonation, replay |
| PII: name, phone | `auth.users`, `/auth/userinfo` | Phishing, privacy breach, regulatory exposure |
| Card metadata: last4, expiry, status, artwork | `app.cards`, Blob Storage | Financial profiling, social engineering |
| Transactions | Table Storage | Financial privacy, fraud intelligence |
| DB / storage credentials | `.env`, Compose environment, process memory | Full data breach |
| CI credentials and registry access | GitHub / registry | Supply-chain compromise |
| Audit trail | application logs | Repudiation, undetected intrusion |

## 3. Actors and trust boundaries

**Actors**

- External unauthenticated attacker (scanner, bot, credential-stuffing operator).
- Authenticated malicious customer (IDOR/BOLA, enumeration, abuse).
- Malicious or careless insider/operator (secrets, production access).
- Supply-chain attacker (dependency, base image, CI action, registry).
- DoS actor (resource exhaustion, slowloris, OTP spam).
- Targeted persistent attacker (financial motive; assume capable, patient).

**Trust boundaries**

```text
Internet
  → nginx (TLS in production; localhost only in the demo)
    → auth  (password + OTP, JWT signing, JWKS)
    → api   (resource API; verifies JWTs via auth JWKS)
      → MSSQL (cards, issuer sync state)
      → Azure Table (transactions)
      → Azure Blob (card artwork)
      → issuer-mock (untrusted external payloads)
    auth → sms-mock (untrusted delivery provider)
CI → registry → container host
container → host kernel
```

Every arrow is a trust boundary. Data crossing one is validated, authorised, and
logged; nothing is trusted because it is "internal".

## 4. Threat scenarios (STRIDE)

| ID | Threat | Current control in this repo | Required for the reference baseline | Verification |
|---|---|---|---|---|
| S1 | Credential stuffing / password brute force | PBKDF2 hashes; per-challenge OTP attempts | Per-IP and per-account rate limiting, lockout/backoff, generic auth errors, MFA/step-up for high-risk actions | Abuse-case tests: 429 + `Retry-After`; no user enumeration |
| S2 | Token forgery | RS256, JWKS, `iss`/`aud`/`exp` validation, `MapInboundClaims=false` | Key rotation and revocation strategy; reject unknown `alg`/`kid` | Tests: bad signature, wrong issuer/audience, expired, `alg=none` |
| S3 | Token theft / replay | 30-minute TTL; no refresh | Short TTL, rotation, revocation on logout/compromise; consider BFF + httpOnly cookie | Tests: replay after revocation; logout invalidates session |
| S4 | IDOR / BOLA | Per-user scoping in handlers; org policy | Object-level authorisation tests for every resource; 403/404 must not leak existence | Tests: other user's card, unknown card, wrong org |
| S5 | Injection (SQL/NoSQL/header) | EF Core/Table SDK parameterisation; FluentValidation | Review every raw query/string interpolation; fuzz malformed payloads | Tests: malformed IDs, oversized input, injection strings |
| S6 | Repudiation | Structured logs, no stack traces to clients | Immutable/centralised audit trail for auth and authorisation events | Tests: required fields present; log retention/export configured |
| S7 | Information disclosure (PII/credentials) | Phone/token redaction; no PAN/CVV/PIN | `traceId` in ProblemDetails; scrub logs and telemetry; no secrets in errors | Tests: logs contain no password/OTP/token/phone; response has `traceId` |
| S8 | Secret leakage | `.env` git-ignored; `.dockerignore` excludes keys | Secret manager/workload identity in production; CI secret scanning; no secrets in images | Scan repo/image history; test that startup fails without secrets |
| S9 | Denial of service | None (demo) | Rate limiting, request timeouts, body-size limits, resource limits, queue backpressure | Load/abuse tests; 429/503 with `Retry-After` |
| S10 | Elevation of privilege | Default-deny `FallbackPolicy`; named `ApiUser` policy; org requirement | Policy/authorisation matrix tests; separate ops/admin plane | Tests: anonymous, wrong org, wrong role |
| S11 | SSRF / unsafe outbound call | Issuer URL comes from configuration, not user input | Egress allowlist, no user-controlled URLs, timeouts/circuit breaker | Review + tests for config-only URLs |
| S12 | Supply-chain compromise | Central package versions; CI MediatR/AutoMapper guard | Pinned image digests, SBOM, dependency/image scanning, Dependabot, SHA-tagged images | CI fails on vulnerable/unpinned dependency |
| S13 | Container escape / host compromise | Non-root container user | `read_only`, `cap_drop: [ALL]`, `no-new-privileges`, seccomp, resource limits | Compose/security scan; runtime inspection |
| S14 | OTP brute force / SMS abuse | 3 wrong OTPs destroys the challenge; 60s resend cooldown | Per-IP/per-account limits, alerting, provider spend caps | Abuse-case tests; alert on spike |

## 5. Abuse cases that must become tests

1. 100 login attempts from one IP for one user → `429`, `Retry-After`, no
   difference between "unknown user" and "wrong password".
2. 100 OTP verifications across challenges → `429`; 3 wrong codes destroy the
   challenge; resend cooldown enforced.
3. Request another user's card/transactions/art → `403`; unknown IDs → `404`;
   neither path leaks whether the object exists.
4. Token with wrong `org` → `403`; wrong `iss`/`aud`/signature/`alg` → `401`.
5. Expired token → `401 TOKEN_EXPIRED`; missing token → `401 UNAUTHENTICATED`.
6. Malformed JSON, unknown fields, oversized body → `400`/`413` with the
   ProblemDetails envelope; no stack trace.
7. Slow or concurrent requests → configured timeout, not thread starvation.
8. Logs and telemetry contain no password, OTP, token, phone number, or PAN;
   every error response carries a correlation ID.
9. Artwork responses carry `X-Content-Type-Options: nosniff` and a restrictive
   CSP/sandbox policy; storage URLs are never exposed to the browser.
10. A failed `auth` metadata/JWKS fetch degrades to `503`, not a misleading
    `401 INVALID_TOKEN`.

## 6. Required controls (reference baseline)

| Area | Control | Priority |
|---|---|---|
| AuthN | Default-deny; named policies; explicit anonymous endpoints; MFA/step-up for ops | P0 |
| AuthZ | Object-level checks on every resource; authorisation matrix tests | P0 |
| Rate limiting | Per-IP + per-account on auth and API; `429` + `Retry-After` | P0 |
| Input | FluentValidation at the boundary; size limits; no raw SQL from input | P0 |
| Secrets | No secrets in repo/images/logs; secret manager/workload identity in production | P0 |
| Transport | TLS at the edge; HSTS; forwarded-header trust only from the ingress | P0 (prod) |
| Headers | `nosniff`, CSP, `frame-ancestors`, `Referrer-Policy`; art sandboxed | P1 |
| Logging | Structured, PII-redacted, correlation ID, centralised audit trail | P0 |
| Supply chain | Pinned versions/digests, SBOM, dependency + image scans, SHA tags | P0 |
| Containers | Non-root, read-only rootfs where possible, `cap_drop`, resource limits | P1 |
| Data | Least-privilege DB/storage identities; encryption at rest; retention policy | P0 |
| Operations | Health/readiness, startup validation, timeouts, incident runbook, pen test | P0 |

## 7. Demo exceptions and production gap register

These are acceptable **only** in localhost Compose. Each must be replaced before
an internet-facing deployment.

| Demo shortcut | Risk | Production replacement |
|---|---|---|
| Plain HTTP on localhost | Token/credential interception | TLS at ingress, HSTS, secure cookies |
| Token in `sessionStorage` | XSS token theft | BFF + httpOnly/Secure/SameSite cookie, or short-lived tokens with rotation |
| In-memory challenge store | Restart drops challenges; no horizontal scale | Distributed challenge store with TTL |
| No revocation / no refresh token | Stolen token valid for 30 minutes | Revocation list/session store, rotation, logout invalidation |
| Rate limit only per challenge | Credential stuffing / OTP spam | Per-IP + per-account limits, adaptive lockout, alerting |
| Azurite well-known key | Full storage access if exposed | Managed identity or scoped SAS; no shared key |
| `sa` for migrations | Excess privilege | Dedicated migrator login scoped to its schema |
| Fixed demo OTP / public demo password | Anyone can log in | Real IdP, MFA, `DEMO_MODE` hard-off in production |
| `sms-mock` logs plaintext OTP | OTP leakage in logs | Real SMS provider; never log OTP |
| `issuer-mock` API key | Feed spoofing/tampering | mTLS or OAuth client credentials, payload signing |
| Published `127.0.0.1` ports | Local exposure only | No host ports; ingress/private network only |
| No WAF / SIEM / pen test | Undetected attacks | WAF, SIEM/alerts, annual pen test, bug bounty if applicable |

## 8. Review triggers

Update this threat model and its tests when any of these changes:

- a new endpoint, verb, or data field;
- a new data store, external service, or outbound URL;
- authentication, token, session, or authorisation changes;
- a new class of PII or financial data;
- a new dependency, base image, or CI action;
- a deployment topology change (ingress, TLS, network policy, registry).

A PR that touches a trigger without updating this document and the corresponding
abuse-case tests should not merge.

## 9. Verification and evidence

- Threat-model review recorded in the PR (link to this file and the changed
  section).
- Abuse-case tests from §5 running in CI.
- Dependency/image scans and SBOM generation in CI.
- Security headers and auth error envelopes asserted by contract tests.
- Penetration test report and remediation plan before production.
- Incident-response runbook with contacts, containment, and key-rotation steps.

## 10. Open questions

1. **PCI DSS scope:** confirm that no PAN/CVV/PIN will be introduced; if they
   will, design tokenisation and scope reduction first.
2. **MFA/step-up:** which actions require it (login, new device, high-value
   transaction, profile change)?
3. **WAF/SIEM:** which vendor/stack, and who triages alerts?
4. **Key management:** managed HSM/KMS for the JWT signing key; rotation cadence.
5. **Data retention:** how long are PII, transaction, and audit records kept?
6. **Fraud monitoring:** is transaction anomaly detection in scope for the
   reference architecture or a separate system?
7. **Compliance regimes:** APRA/ASIC/privacy obligations for the target market;
   legal review before production.
