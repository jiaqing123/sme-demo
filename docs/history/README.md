# Archived handoffs

- **Status:** historical record, frozen
- **Scope:** the PR-by-PR handoff notes written while the baseline was built
  (PR 1 to PR 6), plus the pre-PR-1 baseline snapshot.
- **Not:** current documentation, a decision record, or a description of the
  code as it is today.

These files are kept because they record what was known, decided, and left open
at each point in the project, and because two accepted ADR references cite the
baseline snapshot. They were previously kept in the git-ignored `progress/`
directory, which meant a reader of the repository could not follow those
references.

## Reading rules

1. Read [`../reference/adr/README.md`](../reference/adr/README.md),
   [`../delivery.md`](../delivery.md), and the current `docs/` pages first. Those
   are maintained; these files are not.
2. Treat every count, commit SHA, decision, and "next step" here as **accurate
   at the time of writing and possibly wrong now**. In particular, the Azure
   decisions were open in PR 3–6 and are settled by
   [ADR-020](../reference/adr/ADR-020-azure-hosting-and-test-deployment.md).
3. Do not edit an archived handoff to correct it. If it is cited by current
   documentation, fix the citation and say what changed; otherwise leave the
   history alone.
4. Paths inside these files refer to the layout at the time (`progress/...`,
   `handoff-prN-complete.md`). Those files now live in this directory, and the
   current documents live in `docs/`. Follow the intent, not the literal path.

## Index

| File | Covers | Superseded by |
|---|---|---|
| [`handoff-reference-baseline.md`](handoff-reference-baseline.md) | The state and scope just before PR 1: conventions, decisions, and the work items that became PR 1. | PR 1 and the documents it created |
| [`handoff-pr1-complete.md`](handoff-pr1-complete.md) | PR 1: OpenAPI artefact, contract and architecture tests, CI gates, Azure skeleton, reference docs. | PR 2 onward |
| [`handoff-pr2-complete.md`](handoff-pr2-complete.md) | PR 2: ProblemDetails, rate limiting, readiness, metadata 503 mapping, status-code matrix. | PR 3 onward |
| [`handoff-pr3-complete.md`](handoff-pr3-complete.md) | PR 3: image tags and digests, SBOM + Trivy, Compose hardening, telemetry, ADR-018. | PR 4 onward |
| [`handoff-pr4-complete.md`](handoff-pr4-complete.md) | PR 4: chiseled runtime images, health probe, auth `/keys`, ADR-019. | PR 5 onward |
| [`handoff-pr5-complete.md`](handoff-pr5-complete.md) | PR 5: the delivery cadence, weekly scan, PR template, branch-protection checklist. | [`../delivery.md`](../delivery.md) |
| [`handoff-pr6-complete.md`](handoff-pr6-complete.md) | PR 6: `IssuerSyncService` fix, metadata probes, OpenAPI `servers`, full-history secret scan. | [`../azure-pipelines.md`](../azure-pipelines.md), ADR-020 |

The committed history of each change is the git log; these notes are the
narrative around it, not the evidence.
