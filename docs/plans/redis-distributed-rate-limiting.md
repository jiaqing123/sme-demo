# Plan: shared rate limiting and replica-safe auth state

- **Status:** Proposed — not started, not a decision record
- **Date:** 2026-11-14
- **Owner:** Architecture
- **Depends on:** ADR-017 (per-instance limiter), ADR-020 (Container Apps, one
  replica per app)
- **Estimated size:** 4 pull requests, roughly 6–9 working days, 3–5 agent
  conversations

This is a plan, not a commitment. It exists so the next conversation can start
with the scope and the open decisions instead of rediscovering them. Nothing in
this document has been implemented; `minReplicas = maxReplicas = 1` remains the
current rule in [`infra/main.bicep`](../../infra/main.bicep) and is still correct.

## 1. The problem

The runtime controls that make the demo safe are **per process**:

| Control | Implementation | What breaks with two replicas |
|---|---|---|
| `auth` login/verify/resend limits, per-account login limit | `AddRateLimiter` with fixed-window partitions and in-memory counters (`src/SmeDemo.Auth/Program.cs`, `Auth/LoginAccountRateLimiter.cs`) | Budgets are multiplied by the replica count; a caller can retry against the other replica |
| `api` per-user, per-endpoint limit | fixed-window partitions in `src/SmeDemo.Api/Program.cs` | Same; the effective budget doubles |
| OTP challenges | `ChallengeStore` is a `ConcurrentDictionary` (`src/SmeDemo.Auth/Challenges/ChallengeStore.cs`) | Login creates the challenge on one replica and verify hits another: `404`/`400` and a broken sign-in |
| Readiness snapshot | 2-second in-process cache (`src/SmeDemo.Api/Readiness/ReadinessService.cs`) | Harmless: each replica answers for itself |
| JWT metadata / JWKS | `ConfigurationManager` inside JwtBearer (`src/SmeDemo.Api/Auth/JwtBearerSetup.cs`) | Each replica refreshes on its own schedule; a key rotation is visible at different times |

The `auth` row is the important one: rate limiting alone does **not** make
`auth` horizontally scalable. A shared limiter without a shared challenge store
would still break login. Any plan that promises "two auth replicas" must include
both.

## 2. Goal and non-goals

**Goal:** allow more than one replica of `api` and `auth` without weakening the
controls, and give operators a documented failure mode when the shared store is
unavailable.

**Non-goals:**

- Multi-region, active/active, or a gateway-level limiter.
- A Redis-backed session store: tokens stay stateless.
- Changing the login contract, the OTP flow, or the published demo credentials.
- Replacing Azure Files for signing keys; a shared key volume already works for
  multiple replicas.
- Scaling `web`, `sms-mock`, or `issuer-mock` (they hold no per-user state).

## 3. Design options

### 3.1 Where the shared state lives

| Option | Verdict |
|---|---|
| **Redis** (Azure Cache for Redis, or the `redis` container locally) | **Recommended.** Atomic `INCR` + `PEXPIRE` via a Lua script gives a correct fixed window; a hash with TTL covers challenges; one dependency for both. |
| Azure Cosmos DB / SQL counter table | Rejected. Higher latency per request, more code, and the demo already has SQL for data, not counters. |
| Sticky sessions to one replica | Rejected. Hides the problem, breaks on restart, and Azure Container Apps ingress affinity is not a correctness mechanism. |
| Keep in-memory and document "one replica only" | The status quo; acceptable indefinitely for the demo, but it blocks the Redis item from ever being closed. |

Redis is one more service in Compose and one more Azure resource. It is small,
well understood, and can be made optional for local work (see 3.4).

### 3.2 Limiter store

- Introduce an abstraction owned by `Application` (the hosts already use that
  pattern), for example `IRateLimitCounterStore` with
  `IncrementAsync(key, window, cancellationToken)` returning the count and the
  window reset time.
- Keep the existing in-memory implementation as the default and as the test
  double; add a Redis implementation selected by configuration
  (`RATE_LIMIT_STORE=memory|redis`, `REDIS_URL`).
- Key schema: `rl:{policy}:{partitionKey}:{windowStart}` where the partition key
  is the existing per-IP / per-account / per-user-endpoint key. TTL is the
  window plus a small grace period; never rely on the process clock for the
  window boundary, derive it from the Redis server time inside the Lua script.
- The rejected-response path must still produce the existing
  `429` + `Retry-After` ProblemDetails so the contract tests keep their
  expectations.

### 3.3 Challenge store and key rotation

- `ChallengeStore` becomes an interface with two implementations: the current
  in-memory one and a Redis one (`HSET` with `PEXPIRE`, single-use verify via a
  Lua script that checks attempts, deletes on success, and enforces the resend
  cooldown).
- Keep the same TTL (5 minutes), attempt limit (3), and cooldown (60 seconds);
  the existing auth tests become the contract for both implementations.
- `auth` must not start with more than one replica unless
  `CHALLENGE_STORE=redis`. Fail fast rather than silently break OTP.
- JWKS staleness: either shorten `AutomaticRefreshInterval` to the agreed
  window (simple, no new mechanism) or add an explicit cache-busting signal. The
  simple option is recommended until auth actually rotates keys under traffic.

### 3.4 Failure mode when Redis is unavailable

Three defensible choices, each with a different security position:

| Mode | Behaviour | Trade-off |
|---|---|---|
| **Fail closed (`503`)** | Requests that need the limiter are refused | Safe, but a cache outage stops login and the API |
| **Fail open** | Requests are allowed, logged, and counted locally | Availability preserved; abuse window is unbounded during the outage |
| **Local fallback** | Per-replica in-memory window with the same budget | Degrades to today's behaviour; budgets multiply during the outage |

Recommendation: **local fallback plus a warning log and a metric**, because it
preserves the demo's availability while keeping a bound; the ADR must state
that the bound is per-replica during an outage. This needs the user's decision
(it is a security-control failure mode, not an implementation detail).

## 4. Delivery plan

Four pull requests, each with its own verification. The estimates are working
days for one engineer (or one agent conversation per PR), including tests and
docs.

### PR-A — abstraction and parity (1–2 days, ~300 lines)

- Add `IRateLimitCounterStore` and `IChallengeStore` with the in-memory
  implementations moved behind them; no behaviour change.
- Keep the existing tests passing unchanged; add store-level unit tests
  (window boundary, TTL, attempt counting, single use, cooldown).
- Update ADR-017 with a "future shared store" section and the selected config
  names. No new dependency yet.

### PR-B — Redis implementations (2–3 days, ~400 lines)

- Add `StackExchange.Redis` (pinned, one package) to `Infrastructure`.
- Implement the two Redis stores with Lua scripts, key prefixes, and a
  connection health check; wire `RATE_LIMIT_STORE` / `CHALLENGE_STORE` in both
  hosts.
- Add the `redis` service to `docker-compose.yml` (hardened like the others:
  read-only root where possible, capability drop, no published port) and to
  `infra/main.bicep` (Azure Cache for Redis, private as a follow-up).
- Tests: unit tests for key/TTL logic against a fake; integration tests gated by
  `REDIS_URL` (skipped when unset); a Compose smoke check that two `api`
  containers share one budget.
- Update `docs/development.md` (how to run with Redis) and the runbook failure
  playbook.

### PR-C — replica rollout and metadata freshness (2 days, ~200 lines)

- Raise `maxReplicas` for `api` and (only after the challenge store is shared)
  `auth` in `infra/main.bicep`; add the Redis connection string to Key Vault and
  the Container App secrets.
- Decide and implement the JWKS refresh window; add a test that an auth restart
  is picked up within the documented window.
- Extend [`tools/deploy/environment-smoke.sh`](../../tools/deploy/environment-smoke.sh)
  or add a replica-specific check that a challenge created through one replica
  verifies through another (two API replicas behind the same ingress).
- Update ADR-020's replica statement and the delivery/runbook text that still
  says "one replica until a shared limiter exists".

### PR-D — hardening and close-out (1–2 days, ~150 lines)

- Load/capacity check: document the Redis sizing, the key count per minute, and
  the eviction policy (`noeviction` is wrong for counters; use `allkeys-lru`
  with a documented memory bound).
- Add the Redis resource to the release checklist and the cost note.
- Close ADR-017's production gap with a new ADR (or a superseding section) and
  update the ADR index.

## 5. Acceptance criteria

1. Two `api` replicas enforce **one** shared per-user budget: the N+1st request
   is `429` regardless of which replica answers.
2. A challenge created on replica A verifies on replica B; single use, attempt
   limit, and cooldown still hold.
3. Restarting a replica does not reset a window or lose a pending challenge.
4. With Redis unavailable, the chosen failure mode is observable (log + metric)
   and documented; the smoke tests show the documented behaviour rather than an
   unhandled exception.
5. `docker compose` still starts the demo on a laptop; Redis is either part of
   the default stack or an explicit profile, and the README says which.
6. CI stays green: the five existing check names are unchanged; any new
   integration job is additive and does not slow the PR loop materially.
7. ADR-017 (or its successor) records the failure mode, the key schema, and the
   replica-count rule; `docs/delivery.md` and the runbook are updated.

## 6. Risks

- **Scope creep into "make auth stateless".** The challenge store and the key
  volume are the boundary; do not start rewriting sessions.
- **Testcontainers vs CI.** The `dotnet` CI job has no Docker service. Keep unit
  tests pure and put the Redis integration proof in the Compose smoke job (or a
  new job), otherwise the PR loop becomes slower for everyone.
- **Azure Cache for Redis cost and networking.** Basic/Standard tiers are cheap
  but public; a private endpoint is another VNet item. Decide whether the Azure
  cut is allowed to stay public with TLS + firewall for the demo.
- **Two sources of truth during migration.** Do not keep both stores active;
  select one at startup and fail fast on a half-configured host.
- **Compressible windows.** Redis `PEXPIRE` plus a Lua script is atomic, but a
  badly chosen key (no window start) turns a fixed window into a sliding one.
  Pin the key schema in the ADR and test the boundary.

## 7. Decisions needed before PR-A

1. **Failure mode** when Redis is down: fail closed, fail open, or local
   fallback (recommended: local fallback + warning).
2. **Is Redis mandatory in the default Compose stack**, or an optional profile?
   (Recommendation: optional profile `--profile redis` for local work, always
   present in Azure.)
3. **Which hosts scale first**: `api` only (recommended, smallest risk) or
   `api` + `auth` in one change.
4. **JWKS refresh**: shorten the window (recommended) or add an invalidation
   signal.
5. **Azure Redis tier and network**: Basic/Standard public with TLS + firewall
   for the demo, or add the private endpoint now.

Answer these and the work splits cleanly into the four PRs above.
