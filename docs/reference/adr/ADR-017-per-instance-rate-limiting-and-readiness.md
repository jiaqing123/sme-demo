# ADR-017 — Per-instance rate limiting and readiness; explicit forwarded-header trust

- **Status:** Accepted
- **Date:** 2026-11-13
- **Owner:** Architecture
- **Deciders:** Architecture, Security

## Context

The runtime baseline added fixed-window rate limits to `auth` and `api`, a
dependency readiness probe at `GET /readyz`, and an opt-in forwarded-header
setting for `auth`. These controls live inside one process: the limiters are
in-memory, `/readyz` reports only what the local host can reach, and
forwarded-header trust is only safe when the ingress is the sole path to
`auth`.

The demo runs one replica of each service behind nginx. Production adaptations
will usually run more, and will have a real load balancer or ingress in front.
Without a recorded decision, a later change could treat the demo's budgets as
global or enable forwarded headers on a directly reachable service.

## Decision

1. **Rate limits are per-instance.** `auth` applies per-IP budgets to login,
   verify, and resend, plus a per-account login budget. `api` applies one
   per-user, per-endpoint budget to `/api/v1`. Both use the built-in
   `Microsoft.AspNetCore.RateLimiting` fixed-window limiter and keep partitions
   in process memory. A restart resets them; replicas do not share them.
2. **Readiness is per-instance.** `GET /readyz` checks SQL Server, Blob Storage,
   and the auth metadata document in parallel under a 5 second budget and returns
   `503` with `Retry-After: 5` when any is unavailable. `GET /healthz` stays
   liveness only and does not gate on dependencies. Compose's `api` healthcheck
   and the Compose smoke script use readiness; `auth` keeps liveness.
3. **Forwarded-header trust is explicit and Compose-only.**
   `AUTH_TRUST_FORWARDED_HEADERS` defaults to `false`. Compose sets it to `true`
   because nginx is the only intended browser ingress and appends `X-Forwarded-For`; when true the
   host trusts one forwarded hop and clears the default known proxies/networks.
   A production deployment must instead scope trusted proxies/networks to the
   exact ingress and must not expose `auth` directly.

These are deliberate runtime controls, not a claim of horizontal scalability.
Production must replace the in-memory limiters with a shared limiter (or size
per-instance budgets for the replica count) before relying on them across
replicas.

## Consequences

- The demo gets bounded login, OTP, and API abuse without adding a Redis or
  gateway dependency.
- A restart or a new replica starts with fresh budgets; this is documented as a
  demo caveat, not hidden behaviour.
- Readiness failures do not restart a healthy process. An orchestrator sees
  `api` unhealthy and withholds traffic while `/healthz` still reports liveness.
- Enabling forwarded headers without a trusted-proxy boundary lets a caller
  spoof the per-IP key. The demo's published `127.0.0.1:8081` is exactly that
  risk locally, so the flag stays off by default.

## References

- [`../../requirements.md`](../../requirements.md) §5.4, §7.2, §10.1, §10.3,
  §11.2–§11.3
- [`../../threat-model.md`](../../threat-model.md) §4 (S1, S9, S14), §5, §7
- [`../../development.md`](../../development.md#health-and-readiness),
  [auth rate limits](../../development.md#auth-rate-limits),
  [API rate limits](../../development.md#api-rate-limits), and
  [forwarded headers](../../development.md#forwarded-headers)
- Implementation: [API `Program.cs`](../../../src/SmeDemo.Api/Program.cs)
  (readiness and limiter) and [auth `Program.cs`](../../../src/SmeDemo.Auth/Program.cs)
  (forwarded headers and limiter)
