# Plans and proposals

- **Status:** living index
- **Last updated:** 2026-11-14

A plan describes intended work and its open decisions. It is not a decision
record and it is not evidence that anything works; an accepted decision belongs
in [`../reference/adr/`](../reference/adr/README.md), and the behaviour belongs
in the tests and the runbook.

| Plan | Status | Summary |
|---|---|---|
| [`redis-distributed-rate-limiting.md`](redis-distributed-rate-limiting.md) | Proposed | Shared rate-limit counters and challenge state so `api` and `auth` can run more than one replica; four PRs, 6–9 days; five decisions open. |
