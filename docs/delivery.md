# Delivery: change, weekly, and release loops

- **Status:** operating model for this repository
- **Owner:** Architecture, Platform
- **Last updated:** 2026-11-14
- **Applies to:** every change merged to `main`, every scheduled security scan,
  and every release built from this repository.

This document says **when** the gates run. It does not replace the requirements,
the threat model, the ADRs, or the Definition of Done; those say **what** must be
true. It is deliberately tool-independent: DeepSeek Harness, GitHub Actions, Azure
Pipelines, a human with a terminal, or another coding agent all follow the same
three loops.

## 1. The three loops

| Loop | Trigger | What runs | Output |
|---|---|---|---|
| **Every change** | Pull request or push to `main` | The five CI jobs in [`.github/workflows/ci.yml`](../.github/workflows/ci.yml): .NET build/tests/format/OpenAPI/migrations/vulnerability/secret scan, web build/tests, Compose validation, image build + SBOM + Trivy, Compose smoke | A reviewed commit on `main` that is a release candidate; never a deployment by itself |
| **Every week** | Monday Dependabot schedule; Tuesday 03:00 UTC security scan | Grouped dependency PRs, and the scheduled rebuild-and-scan workflow [`.github/workflows/security-scan.yml`](../.github/workflows/security-scan.yml) | Updated dependencies and a fresh vulnerability answer even when no code changed |
| **Every release** | On demand, or a fixed window agreed with the demo audience | Promotion of an already-gated image digest, environment deployment, environment smoke, recorded evidence | A deployed, reproducible version that can be rolled back by digest |

PRs are **not** releases. Merging to `main` means "this commit is a release
candidate"; it does not mean it was deployed.

## 2. Every change: pull request

1. Branch from `main`; do not commit directly to `main`.
2. Make the smallest coherent change. If it changes a contract, data model,
   trust boundary, or decision, update the matching document in the same PR
   ([`requirements.md`](requirements.md), [`threat-model.md`](threat-model.md),
   ADR index in [`reference/adr/README.md`](reference/adr/README.md)).
3. Run the local pre-flight (or let CI run it, but do not ask a reviewer to find
   a red build):

   ```powershell
   dotnet tool restore
   dotnet build SmeDemo.slnx --configuration Release
   dotnet test SmeDemo.slnx --configuration Release --no-build
   dotnet format SmeDemo.slnx --verify-no-changes
   docker compose config -q
   python tools/smoke/check-vulnerabilities.py   # python3 on Linux/macOS
   ```

   Compose needs `.env`; create it once from `.env.example` without overwriting
   an existing file. The full local list, including migrations, generated SQL,
   and OpenAPI regeneration, is in
   [`development.md#what-you-can-verify-locally`](development.md#what-you-can-verify-locally).
4. Open the PR using the repository template. Complete
   [`reference/definition-of-done.md`](reference/definition-of-done.md) honestly;
   the checklist is the review contract, not decoration.
5. CI must be green. The five job names are `.NET build and tests`,
   `Web build and tests`, `Compose validation`,
   `Build and scan container images`, and `Compose smoke test`; when branch
   protection is enabled (§5), require these exact checks.
6. At least one reviewer approves. Review the diff for secrets, `bin/`/`obj/`,
   generated files that should not be committed, and a weakened security control.
7. Merge only after the checks are green and the review comments are resolved.

**Cost control:** the full image build, Trivy scan, and Compose smoke run on
every push/PR today. At this size that is a few minutes and is worth it. If the
repository grows, move the image build + scan to `main` or a nightly job, but keep
the fast build/test/format gates on every PR. Do not remove the image scan from
the release path.

## 3. Every week: dependency and vulnerability review

### Monday: Dependabot

Dependabot opens grouped PRs for NuGet, npm, GitHub Actions, and Docker base
images (see [`.github/dependabot.yml`](../.github/dependabot.yml)). Each PR runs
the same five CI jobs.

- Merge a grouped PR when CI is green and the diff is understood.
- Docker **major** updates are ignored on purpose. A .NET, Node, or nginx major
  is a work item with its own build, scan, and smoke gate, not a weekly merge.
- A Docker **security fix** that only exists in a new major will not be proposed
  by Dependabot because of that ignore. The Tuesday scan can surface fixable
  findings in the current base, but a fix that only exists in a new major needs
  the manual major work item and its full build/scan/smoke gate.
- Do not merge a red dependency PR by disabling a gate. Fix the dependency, pin a
  compatible version, or record the risk explicitly.

### Tuesday: scheduled container scan

[`.github/workflows/security-scan.yml`](../.github/workflows/security-scan.yml)
rebuilds the six images from the current `main` and runs the same pinned Trivy
gate as CI:

- `HIGH`/`CRITICAL`, `--ignore-unfixed`, `--exit-code 1`;
- a fixable finding is a release blocker until fixed or explicitly risk-accepted;
- `--ignore-unfixed` means unfixed findings are filtered out of this gate, so it
  is not an inventory of them. Run a separate scan without the flag if the
  unfixed inventory is needed for the risk register.

Why a separate schedule: a new CVE in a base image does **not** require a commit.
Without the weekly job, the repository would only learn about it the next time
somebody changed code.

Run it manually from the Actions tab or with
`gh workflow run security-scan.yml` when a CVE is announced or before a demo.

GitHub may disable a scheduled workflow after 60 days without repository
activity. Re-enable it in the Actions tab and run it once; the schedule alone is
not a monitoring system.

### Triage rules

| Finding | Action |
|---|---|
| Fixable `HIGH`/`CRITICAL` in an image | Fix the base image or dependency, rebuild, rescan before the next release |
| Unfixed `HIGH`/`CRITICAL` | Not shown by this gate (`--ignore-unfixed`). If the inventory is needed, run a separate scan without the flag and record the risk with an owner and review date; do not add a permanent ignore |
| Temporary ignore | Only in a time-boxed `.trivyignore.yaml` with an expiry and an owner, as required by ADR-018 |
| Dependency PR with a broken gate | Treat as a failed change; do not bypass |

## 4. Every release

### Cadence

Use a **fixed window plus on-demand releases**, not a release per PR:

- a weekly (or biweekly) window for the demo, once the deployment line exists;
- an extra release before a customer demo or a security fix;
- no release just because a pull request merged.

The demo currently has no real release stage: `azure-pipelines.yml` builds and
verifies, but the Test/UAT/Production stages are excluded by default and
deliberately fail once enabled until the real migration and rollout steps exist.
They stay blocked until the hosting, environment-automation, and
production-approval decisions (1–3 in
[`azure-pipelines.md#current-open-decisions`](azure-pipelines.md#current-open-decisions))
are answered. Until then, "release" means a green `main` and a locally verified
Compose stack, not a deployment.

### Release checklist

A release owner works through this list and records the evidence in the release
notes or the deployment record:

- [ ] The commit on `main` is identified by SHA and its CI run is green.
- [ ] No open fixable `HIGH`/`CRITICAL` finding on the six images.
- [ ] Database migrations are reviewed, committed as idempotent SQL, and
      backward-compatible with the previous application version (expand/contract).
- [ ] The release notes state what changed, the API version impact (ADR-012), and
      any deprecation window.
- [ ] Images are built **once** and pushed to ACR with `publishImages=true` once
      `acrServiceConnection` is configured; record the immutable digest, not only
      the build tag. `acrLoginServer` is needed only when the deployment stages
      reference the image.
- [ ] SBOMs and the Trivy result come from the `publishImages=false` build, which
      is the path that generates them; the publish path skips both. Scan the
      **promoted digest** registry-side again before deployment.
- [ ] Test environment: deploy the digest, run migrations, run the Compose or
      environment smoke, and record the result.
- [ ] UAT: deploy the same digest and complete the acceptance path; no rebuild.
- [ ] Production: requires the agreed approval (Environment approval and/or
      change ticket), deploys the same digest, and runs the post-deploy smoke.
- [ ] The previous digest and the rollback command are written down before the
      first deployment starts.
- [ ] The release record links commit, CI run, image digests, SBOM, scan, smoke,
      and approvals.

### Promotion rules

- **Build once, promote the digest.** Do not rebuild for Test, UAT, or Prod; a
  rebuild is a different artefact and invalidates the scan and SBOM.
- Tags are for humans; digests are the deployment contract.
- A hotfix is a normal pull request from the release commit, with the full CI
  gate and the same promotion path. Do not hotfix by pushing an unverified image
  or by editing a running container.
- Roll back by redeploying the previous digest, not by reverting source and
  rebuilding under pressure. Database migrations must therefore be expand/contract
  so the previous application can still run.

## 5. Branch protection and repository settings

These settings are not stored in Git. A repository administrator must configure
them once (GitHub branch protection or a ruleset for `main`):

- require a pull request before merging;
- require at least one approval and dismiss stale approvals on new commits;
- require conversation resolution;
- require the five CI checks listed in §2;
- require branches to be up to date before merging;
- block force pushes and branch deletion;
- include administrators (or document why not);
- protect release tags (`v*`) against deletion and force updates.

Until these are enabled, CI green is a convention, not an enforced gate. Check
the actual settings before claiming the repository is protected.

GitHub Actions should keep the default `GITHUB_TOKEN` read-only. No workflow in
this repository needs write access to repository contents today.

## 6. Working without DeepSeek Harness

DeepSeek Harness is an execution aid, not part of the delivery system. Nothing in
the release path depends on it.

**The source of truth is the repository:**

| Question | Read |
|---|---|
| How do I build/run/test? | [`development.md`](development.md) |
| What must be true before a PR? | [`reference/definition-of-done.md`](reference/definition-of-done.md) |
| When do gates and releases run? | this document |
| What did we decide and why? | [`reference/adr/README.md`](reference/adr/README.md) |
| How do I operate/roll back? | [`runbook.md`](runbook.md) |
| How is the Azure line wired? | [`azure-pipelines.md`](azure-pipelines.md) |
| What is the system contract? | [`requirements.md`](requirements.md) |

`progress/` is git-ignored and is **not** a delivery record. Handoffs, chat
transcripts, and agent memory are convenience, not evidence. If a decision or a
runbook step matters, it belongs in `docs/`, an ADR, a workflow, or the release
record.

A new engineer, or a different coding agent, can continue with this sequence:

1. read [`README.md`](../README.md), then this document;
2. run the local pre-flight in §2;
3. work on a branch, open a PR, let CI prove it;
4. for a release, follow §4 and record the evidence.

If the work is done without an agent, the same commands apply. If it is done with
a different agent, give it `README.md`, this document, and the Definition of Done,
and require it to leave the working tree clean and CI green before merging.

## 7. Current gaps and decisions

These are known and deliberate, not silent omissions:

1. **Azure deployment is a placeholder.** The hosting model and the other four
   decisions in [`azure-pipelines.md#current-open-decisions`](azure-pipelines.md#current-open-decisions)
   must be answered before Test/UAT/Prod stages can be enabled.
2. **No always-on registry in GitHub Actions.** The weekly scan rebuilds images
   from `main`. Once images are pushed to ACR by digest, scan the promoted digest
   and keep the rebuild as a fallback.
3. **Branch protection is manual.** §5 is the checklist; it cannot be enforced
   from this repository.
4. **No release tags yet.** The first real deployment should establish the tag
   format and the release-notes template.
5. **Scheduled workflows can be disabled after inactivity.** See §3.
6. **Docker major updates are ignored by Dependabot.** A security fix that only
   lands in a new major is a manual follow-up; the weekly scan is the backstop.

## 8. Quick reference

```powershell
# Local change gate
dotnet build SmeDemo.slnx --configuration Release
dotnet test SmeDemo.slnx --configuration Release --no-build
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
python tools/smoke/check-vulnerabilities.py

# Full local stack (create .env from .env.example first)
docker compose up -d --build
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans

# Re-run the weekly scan from a workstation with gh
gh workflow run security-scan.yml
```

The commands that reproduce CI, including migrations, generated SQL, and OpenAPI
regeneration, are in [`development.md`](development.md).
