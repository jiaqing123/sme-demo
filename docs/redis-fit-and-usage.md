# Redis Fit and Usage

A decision guide for adding Redis to a system, written from the evidence in
`sme-demo`. The first half says when a shared store is not worth its cost and
when it is unavoidable; the second half gives the pattern for each case: the
mechanism, the key schema, the script shape, and the failure mode.

This is not a proposal to add Redis to `sme-demo`. ADR-017 remains the accepted
decision, and the migration plan is proposed only. The demo is the "don't" case
and the failure evidence for the "must" case.

## 1. Summary: the decision in one screen

| Reach for Redis when | Do not add Redis when |
|---|---|
| More than one process must enforce **one** counter, and a multiplied budget is a security failure. | There is one replica, or the per-instance bound is a recorded, accepted limit. |
| State is **one-time and cross-process** (a challenge, a nonce, a token), and the process that created it must not be the only one that can consume it. | The state dies with the process harmlessly, or the whole flow runs in one process. |
| A cache must become **fresh across processes within a bounded time**, and a stale entry produces a wrong answer rather than a slower one. | Staleness is harmless, or every process can refresh on its own within the tolerance you have defined. |
| You can operate one more dependency: its secret, its network path, its failure mode, and its cost. | Adding it would make the system harder to run than the problem it solves. |
| The data is regenerable, and losing all of it is a cache miss. | It is the only copy of something, a job queue, or a correctness lock. |
| You have measured the sizing and chosen a failure mode before production. | The plan is "add Redis later" with no owner, no failure mode, and no test path. |

If a left-column statement is true, go to §3 for the trigger and §4 for the
pattern. If the right column is what fits your system, §2 is the argument for
keeping the in-memory implementation and recording that choice.

## 2. When not to use Redis

`sme-demo` runs one replica of each service. ADR-020 sets
`minReplicas = maxReplicas = 1` for the application services and both mocks, and
gives the reason: "ADR-017's rate limits, challenge store, and readiness cache
are per process; a second replica would multiply the effective budgets and
split the challenge store." Every runtime control the demo relies on is in
process:

| Control | Implementation | State |
|---|---|---|
| `auth` login, verify, resend limits | `AddRateLimiter` fixed windows in `src/SmeDemo.Auth/Program.cs`, policies `auth-login-ip`, `auth-challenge-verify-ip`, `auth-challenge-resend-ip` | Per-IP partitions in memory |
| `auth` login limit per account | `LoginAccountRateLimiter` singleton, called from `AuthFlowService.LoginAsync` with `username.ToLowerInvariant()` | Per-account partitions in memory |
| `api` limit | One fixed window per user and endpoint in `src/SmeDemo.Api/Program.cs`, partition `$"{userId}:{endpoint}"` | Per-partition counters in memory |
| OTP challenges | `ChallengeStore`, a `ConcurrentDictionary<Guid, Challenge>` | 5 minute TTL, 3 attempts, 60 second resend cooldown, single use |
| Readiness | `ReadinessService` with a 2 second snapshot cache and a 5 second probe budget | Per-process snapshot |
| Signing metadata | `JwtBearer`'s `ConfigurationManager` in `src/SmeDemo.Api/Auth/JwtBearerSetup.cs` | Per-process discovery/JWKS cache |

This was the correct call for the demo, not an oversight:

- **The budgets are small and the traffic is one container.** A per-IP or
  per-account limit still bounds abuse; only the global guarantee is lost.
- **A restart resetting a budget is an accepted consequence.** ADR-017: "A
  restart or a new replica starts with fresh budgets; this is documented as a
  demo caveat, not hidden behaviour." Pending challenges are documented the same
  way (`ChallengeStore`).
- **The dependency would cost more than it buys.** Redis means a container or
  managed resource, a secret, a network path, a backup and eviction policy, and
  an outage that breaks login, for no benefit at one replica.
- **The scalability limit is recorded, not hidden.** ADR-017 calls these
  "deliberate runtime controls, not a claim of horizontal scalability" and
  ADR-020 repeats the replica rule. A recorded trade-off is a decision; an
  unrecorded one is a future incident.

The rule: do not add Redis because the system might scale. Add it when a control
stops holding at the replica count you are about to run.

## 3. The three shapes of cross-process state

Each one produces a concrete failure in `sme-demo` at two replicas. The first
two require shared state; the third requires a freshness decision: a bounded
refresh interval or a shared signal.

### 3.1 Shared counters

Both limiters keep a fixed-window counter per partition inside the process, so a
second replica starts with its own empty counter for the same subject. The
failure is not "the limit is slightly looser": the default API budget of 120
requests per minute per user and endpoint becomes up to 240 per minute across two
replicas, and the per-account login budget of 10 per 5 minutes becomes up to 20.
Enforcement also becomes probabilistic. A caller rejected by replica A can retry
immediately on replica B and succeed, and A's `Retry-After` describes a window B
does not share. The security property "N attempts per window per subject" no
longer exists, regardless of which replica answers.

ADR-017 allows sizing per-instance budgets for a fixed replica count as an
interim. That keeps a per-instance bound, not one global bound: fixed windows are
not partitions of a shared budget, traffic is uneven, and one replica rejects
early while another has headroom. Record it as an interim, or move to a shared
store. The trigger is a global bound, not a guess at the replica count.

### 3.2 One-time cross-replica state

`POST /auth/login` creates the challenge in the `ChallengeStore` of the `auth`
process that handled it, and returns a random `Guid` id. Nothing routes that id
back, so `POST /auth/challenges/{id}/verify` can arrive at the other replica,
whose dictionary has no such key.

`AuthFlowService.VerifyAsync` maps both `NotFound` and `Expired` to
`ApiProblem.Unauthenticated("The challenge has expired or is no longer valid.")`,
so the caller gets `401 UNAUTHENTICATED` instead of a token. The plan's summary
table writes this as `404`/`400`; the current code maps it to 401. `Resend`
fails the same way, and a restart of the replica that created the challenge
loses it outright.

Rate limiting alone cannot fix this. A shared limiter without a shared challenge
store still breaks sign-in, and the plan says so: "rate limiting alone does
**not** make `auth` horizontally scalable."

### 3.3 Cross-replica cache invalidation

Each `api` process owns a `ConfigurationManager` inside `JwtBearer` and refreshes
the discovery document and JWKS on its own schedule; `auth` can rotate a signing
key and publish it, and replicas learn about it at different times.

The failure is intermittent authentication. A token signed with a key one replica
has not fetched yet fails signature validation there, and `JwtBearerSetup` maps
the resulting `SecurityTokenSignatureKeyNotFoundException` to
`401 INVALID_TOKEN`; the same token succeeds on a replica that has refreshed. A
user sees a request fail once and succeed on retry, and the failure moves between
replicas as their refresh timers drift. The stock handler does not bound that
drift tightly: an unknown key id triggers a refresh subject to
`RefreshOnIssuerKeyNotFound`, and every refresh is subject to `RefreshInterval`
and `AutomaticRefreshInterval`; confirm the defaults and minimums for your
IdentityModel version. [`development.md`](development.md) records the same
window: a stale cache returns `401 INVALID_TOKEN` for an unknown key until it
refreshes. The inverse also holds: a retired key stays acceptable on a replica
that has not refreshed, for as long as its cache says so.

A cache is not automatically a trigger. The readiness snapshot is cached for 2
seconds, but each replica answers for itself, so there is nothing to invalidate.
The trigger is a cache whose value must agree across processes.

## 4. How to use it

Each pattern below names the mechanism, the key schema, the script shape, and the
failure mode.

### 4.1 Fixed-window counter

One key per subject per window: `rl:<policy>:<partition>:<windowStart>`, where
`policy` is the existing policy name and `partition` is the existing per-IP,
per-account, or per-user-endpoint key. Construct it, never parse it: a partition
may contain `:`.

```lua
-- KEYS[1] = rl:{policy:partition}, one hash slot per subject
-- ARGV[1] = window in seconds, ARGV[2] = grace in seconds
local t = redis.call('TIME')
local now = tonumber(t[1])
local window = tonumber(ARGV[1])
local start = now - (now % window)
local key = KEYS[1] .. ':' .. start

local count = redis.call('INCR', key)
if redis.call('PTTL', key) == -1 then
  redis.call('PEXPIRE', key, math.floor((window + tonumber(ARGV[2])) * 1000))
end
return { count, (start + window) - now, redis.call('PTTL', key) }
```

Use server time inside the script, never the process clock: replicas whose clocks
differ compute different boundaries, write different keys, and reproduce the
split-budget bug. `TIME` returns `{seconds, microseconds}` as strings.

Put the window start in the key. A single key per subject with a TTL set on
creation anchors the window to the first request after idle; refreshing the TTL
makes it sliding rather than fixed. Either mistake is silent at one replica, and
`windowStart` makes replicas agree and `Retry-After` meaningful.

Set the TTL to the window plus a small grace, and derive `Retry-After` from the
script's reset time, not `PTTL`: the grace keeps a key written late in its window
alive, but the reset stays `windowStart + window`. The value is numeric seconds,
at least 1, matching `Math.Max(1, (int)Math.Ceiling(...))` in
`ProblemResponseWriter`, and the 429 keeps the `RATE_LIMITED` ProblemDetails.

Key placement is where a cluster deployment fails. Use a hash tag around the
subject - `rl:{<policy>:<partition>}:<windowStart>` - so every window for one
subject lands on one slot, and confirm the dynamic key construction with your
client and topology. The ACL must allow `EVAL`/`EVALSHA` plus every command a
script calls: `INCR`, `PEXPIRE`, `PTTL`, and `TIME` here; `HMGET`, `HSET`, and
`DEL` in §4.2. `TIME` inside a script assumes effect replication, the default
from Redis 5.

### 4.2 One-time challenge or token

Store the challenge as a hash with a TTL, not a value plus a separate expiry key:
`otp:{challengeId} -> user_id, code, attempts, resend_at`, TTL 300 seconds.

The semantics must match `ChallengeStore`: 5 minute TTL (`ChallengeStore.Ttl`),
3 attempts (`MaxAttempts`), 60 second cooldown (`ResendCooldown`), single use.
Create with `HSET` and `PEXPIRE` in one script, never two round trips, because a
process that dies between them leaves a challenge with no expiry. `resend_at` is
`TIME + 60` from the server clock, not a process-clock `expires_at` field.

Verify in one script that checks the code, decrements attempts, and deletes on
success:

```lua
-- KEYS[1] = otp:{challengeId}, ARGV[1] = submitted code
local key = KEYS[1]
local values = redis.call('HMGET', key, 'user_id', 'code', 'attempts')
if not values[1] then
  return {'not_found'}
end
local attempts = tonumber(values[3])
if not attempts then
  return {'not_found'}
end
if attempts <= 0 then
  redis.call('DEL', key)
  return {'too_many'}
end
if values[2] ~= ARGV[1] then
  attempts = attempts - 1
  if attempts == 0 then
    redis.call('DEL', key)
    return {'too_many'}
  end
  redis.call('HSET', key, 'attempts', attempts)
  return {'invalid', attempts}
end
redis.call('DEL', key)
return {'ok', values[1]}
```

Resend is a third script: compare `resend_at` to `TIME`, refuse inside the
cooldown with the remaining seconds, otherwise `HSET` the new code and
`resend_at` without extending the TTL, matching `ChallengeStore.Resend`.

The script exists for the race, not the round trips. Without it, two concurrent
verifies can both read the same code, both match, and both issue a token before
either deletes the key; two wrong guesses can both read `attempts = 1` and both
decrement without ever exceeding it. `WATCH`/`MULTI` works but costs more code
and retries. A Lua script runs without interleaving, so the success path's `DEL`
is the single-use enforcement, not a flag another request may read too late.

A Lua string comparison is not constant-time; if code timing matters, store an
HMAC of the code and compare digests. If the store is unavailable, challenge
creation and verification fail, so fail-closed is defensible here: login is a
credential endpoint.

### 4.3 Cache with invalidation

Treat the TTL as the safety net and an explicit signal as the freshness
mechanism. The TTL bounds how long a missed invalidation can be wrong; it does
not make the cache fresh. Two signals cover most cases:

- **`DEL` after the write.** When the writer can compute the cache key, delete it
  after the source of truth commits. The next read misses and rebuilds.
- **Pub/sub or a version key.** When the value is cached locally inside each
  process rather than in Redis, a shared `DEL` cannot reach those copies. Publish
  an invalidation, or bump a version key that local caches check before serving.
  Pub/sub is fire-and-forget: a disconnected or restarting replica misses the
  message, which is why the TTL must still exist.

Example: `SET cache:jwks:{issuer} <document> EX 60`, then
`DEL cache:jwks:{issuer}` after a rotation. A missed `DEL` serves the old
document until the TTL expires.

Refreshing every N seconds per replica is not invalidation. It is a staleness
bound you chose, and it is per replica, so two instances can disagree for up to
the whole interval and no event closes the gap. In §3.3 that gap is a valid token
rejected by one replica and accepted by another.

Invalidation also creates a stampede: deleting a hot key or letting it expire
turns one source-of-truth call into as many as there are concurrent readers.
Single-flight inside each process (the pattern `ReadinessService` already uses
with its `SemaphoreSlim`) is the small fix; a shared lock or jittered TTL is the
larger one. Do not use a Redis lock for correctness here, only to reduce load,
and let a failed rebuild fall back to the previous value.

For `sme-demo`'s JWKS case the plan recommends shortening the
`AutomaticRefreshInterval` rather than building a signal, until keys rotate under
traffic. That is a staleness bound, not invalidation, and should be recorded as
one.

### 4.4 Connection handling

- **One multiplexed connection per process.** `StackExchange.Redis`'s
  `ConnectionMultiplexer` is designed to be shared; connect-per-request defeats
  it and multiplies connection pressure on the server.
- **`AbortOnConnectFail = false` is a decision, not a default to copy.** It makes
  the multiplexer keep retrying instead of failing, which lets a host start with
  the store unreachable: the right runtime choice and the wrong startup choice.
  If the ADR says "fail fast at startup", probe the store explicitly with a
  bounded timeout instead of relying on lazy connection behaviour.
- **Bound timeouts and the fallback.** Set command and connect timeouts, never
  call synchronously over async, and do not queue work without a limit. A slow
  store must produce the documented fallback, not an unbounded queue. Measure the
  failure path by taking the store away, not by reading the client docs.
- **Authenticate and encrypt in transit.** Use TLS and a secret from Key Vault,
  as ADR-020 does for the other connection strings, and never log the connection
  string, including on startup failure.

## 5. Anti-patterns

| Anti-pattern | Why it fails | Do instead |
|---|---|---|
| Redis as a job queue | Lists and streams are delivery primitives. Visibility timeouts, acknowledgement, retries, dead letters, and scheduling are yours to build, and a restart or eviction loses pending work. | Use a broker designed for it, or an outbox table plus a poller when the queue can be derived from committed data. |
| Redis as the primary data store | Persistence is not a backup, eviction can remove live data, and there are no schema constraints or multi-key transactions with a relational store's guarantees. | Keep the source of truth in SQL or another durable store; Redis holds what you can rebuild. |
| Redis locks for correctness | A lease can expire during a stop-the-world pause or a partition while the owner still believes it holds the lock. Two writers then proceed. | If mutual exclusion must be correct, use a fencing token that the protected resource checks, and treat the lock as an optimization. |
| Redis as the only copy of anything | A flush, an eviction, or a lost managed instance deletes it. Anything you cannot regenerate does not belong there. | Store the authoritative copy elsewhere and make the Redis copy disposable. |
| Giant keys and unbounded collections | The server is single-threaded per command. `HGETALL`, `SMEMBERS`, `KEYS`, and deleting a huge key block other clients, and the key grows without a bound. | Bound cardinality, shard by subject, use `HSCAN`/`SSCAN`, never `KEYS` in a request path, and delete large keys incrementally. |
| Using Redis to avoid a schema decision | The model moves into application code: no constraints, no joins, no single place that defines the shape. Every client re-implements the invariants. | Decide the model first. Use Redis only for the access pattern it is good at, with a documented key schema and owner. |

## 6. If sme-demo did adopt it

The plan compresses to one shape: seam first, Redis second, parity throughout,
and it is the shape to copy. No part of it has been done here; ADR-017 remains
accepted and `infra/main.bicep` still sets `minReplicas = maxReplicas = 1`.

1. **Add the seam with no behaviour change.** Move the existing behaviour behind
   interfaces owned by `Application`. The plan names `IRateLimitCounterStore`
   with `IncrementAsync(key, window, cancellationToken)` returning the count and
   the reset time, and `IChallengeStore` with the current `Create`, `Verify`, and
   `Resend` semantics. The in-memory implementations stay the default and the
   test double.
2. **Enforce parity before the second implementation exists.** The existing
   contract tests must pass unchanged against the in-memory store: the 429
   envelope, `Retry-After`, the 5 minute TTL, 3 attempts, 60 second cooldown, and
   single use. Add store-level tests for the window boundary and for two
   concurrent verifies; they become the shared contract. A Redis implementation
   that needs a test changed is not parity.
3. **Add the Redis implementation behind configuration.** The plan proposes
   `RATE_LIMIT_STORE=memory|redis`, `CHALLENGE_STORE=memory|redis`, and
   `REDIS_URL`; select exactly one store at startup and never dual-write. Add
   integration tests gated on `REDIS_URL` and a Compose smoke check that two
   `api` containers share one budget.
4. **Fail fast at startup, degrade at runtime.** If a shared store is configured
   and unreachable, refuse to start. If it fails later, fall back to the local
   in-memory limiter, log a warning, and increment a metric; state in the ADR
   that the bound is per replica during the outage. `auth` must not start with
   more than one replica unless `CHALLENGE_STORE=redis`; a silently broken OTP
   flow is worse than a failed deployment.

## 7. Decision checklist

Paste this table into your ADR and replace each "confirm" with your finding. A
row you cannot answer is a reason to delay the dependency, not to assume.

| # | Decision | Recommendation to record |
|---|---|---|
| 1 | Failure mode when the store is down | Local per-replica fallback plus a warning log and a metric at runtime. Fail fast at startup when a shared store is configured and unreachable. Record that the fallback bound is per replica, not global. In a security-sensitive system, consider fail-closed for credential endpoints and name which endpoints those are. |
| 2 | Local development | Optional Compose profile (`--profile redis`) for local work, mandatory in the deployed environment. One CI job must actually run that profile and exercise the Redis path; without it the shared path is untested until production. |
| 3 | Which service scales first | The stateless API first. Do not scale `auth` until both the challenge store and the signing-key volume are shared. Confirm the ingress or upstream supports multiple backends, and that forwarded-header trust still assumes one hop (`ForwardLimit = 1`, with the default known proxies and networks cleared as Compose does). |
| 4 | Key/JWKS refresh | Shorten the refresh interval rather than building an invalidation signal. First confirm the framework's minimum allowed interval for your IdentityModel version; if one minute is rejected, use the smallest accepted value and record it. |
| 5 | Managed tier and network | Standard tier with TLS. Do not rely on public IP allow-listing when the client's egress addresses are not stable. Prefer identity-based authentication if the tier supports it (confirm), or pinned egress, and record a private endpoint as a follow-up with an owner. |

Sizing is a measurement, not a table row. Before choosing a tier, measure the key
count per minute, the working set and key cardinality, the p99 command latency
under the expected concurrency, the connection count, and the error rate when the
store is degraded. The eviction policy is part of the decision: an evicted
counter silently resets a window, so prefer a no-eviction or volatile-TTL policy
for counters and record why if you accept otherwise. The plan's `allkeys-lru`
suggestion for counters needs reconciling with that - LRU eviction of a live
counter is a correctness change, not a cache miss.

## 8. References

- [ADR-017: per-instance rate limiting and readiness](reference/adr/ADR-017-per-instance-rate-limiting-and-readiness.md)
- [ADR-020: Azure hosting and test deployment](reference/adr/ADR-020-azure-hosting-and-test-deployment.md)
- [Plan: shared rate limiting and replica-safe auth state](plans/redis-distributed-rate-limiting.md)
- Implementation: [`src/SmeDemo.Api/Program.cs`](../src/SmeDemo.Api/Program.cs),
  [`src/SmeDemo.Auth/Program.cs`](../src/SmeDemo.Auth/Program.cs),
  [`LoginAccountRateLimiter.cs`](../src/SmeDemo.Auth/RateLimiting/LoginAccountRateLimiter.cs),
  [`ChallengeStore.cs`](../src/SmeDemo.Auth/Challenges/ChallengeStore.cs),
  [`ReadinessService.cs`](../src/SmeDemo.Api/Readiness/ReadinessService.cs)
- [Development guide](development.md) (auth and API rate limits, health and
  readiness) and [Observability](observability.md) (the per-instance limitation)
