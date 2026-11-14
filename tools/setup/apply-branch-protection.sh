#!/usr/bin/env bash
#
# apply-branch-protection.sh — apply the repository settings that cannot live in
# Git: the `main` branch protection rule and the release-tag ruleset.
#
# Usage:
#   tools/setup/apply-branch-protection.sh [--repo OWNER/NAME] [--branch main]
#                                          [--dry-run]
#
#   --repo OWNER/NAME  repository; default: derived from the `origin` remote
#   --branch NAME      protected branch; default: main
#   --dry-run          print the payloads, do not call the API (works without gh)
#   -h, --help         show this help
#
# Requires: gh (authenticated) and a token that can administer the repository.
# Set GH_TOKEN or run `gh auth login` first. A fine-grained token needs
# "Administration: read and write" on this repository.
#
# Idempotent: the branch rule is replaced with the same payload on every run,
# and the tag ruleset is updated when a ruleset with the same name exists.
#
# The five CI check names below are the ones the workflows define. Renaming a
# job in .github/workflows/ci.yml without updating this script silently widens
# the gate, which is why the names are repeated here and in docs/delivery.md §5.

set -euo pipefail

REPO=""
BRANCH="main"
DRY_RUN=false

usage() {
    cat <<'EOF'
apply-branch-protection.sh — apply main branch protection and the release-tag ruleset.

Usage:
  tools/setup/apply-branch-protection.sh [--repo OWNER/NAME] [--branch main] [--dry-run]

Requires gh (authenticated) with repository administration permission.
Set GH_TOKEN or run `gh auth login`. Use --dry-run to print the payloads only.
EOF
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --repo)
            [ "$#" -ge 2 ] || die "--repo requires a value"
            REPO="$2"
            shift 2
            ;;
        --branch)
            [ "$#" -ge 2 ] || die "--branch requires a value"
            BRANCH="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown argument: $1 (try --help)"
            ;;
    esac
done

if [ -z "$REPO" ]; then
    remote_url="$(git remote get-url origin 2>/dev/null || true)"
    [ -n "$remote_url" ] || die "no --repo given and no origin remote to derive it from"
    case "$remote_url" in
        https://github.com/*) REPO="${remote_url#https://github.com/}" ;;
        git@github.com:*)     REPO="${remote_url#git@github.com:}" ;;
        *) die "cannot derive OWNER/NAME from origin '$remote_url'; pass --repo" ;;
    esac
    REPO="${REPO%.git}"
fi

case "$REPO" in
    */*) ;;
    *) die "--repo must be OWNER/NAME (got '$REPO')" ;;
esac

# The exact job names from .github/workflows/ci.yml.
CHECKS_JSON='[
  ".NET build and tests",
  "Web build and tests",
  "Compose validation",
  "Build and scan container images",
  "Compose smoke test"
]'

protection_payload="$(cat <<EOF
{
  "required_status_checks": {
    "strict": true,
    "contexts": $(printf '%s' "$CHECKS_JSON" | tr -d '\n')
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "dismiss_stale_reviews": true,
    "require_code_owner_reviews": false,
    "required_approving_review_count": 1,
    "require_last_push_approval": false
  },
  "restrictions": null,
  "required_conversation_resolution": true,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "block_creations": false,
  "required_linear_history": false
}
EOF
)"

tag_ruleset_payload="$(cat <<'EOF'
{
  "name": "protect-release-tags",
  "target": "tag",
  "enforcement": "active",
  "conditions": {
    "ref_name": {
      "include": ["refs/tags/v*"],
      "exclude": []
    }
  },
  "rules": [
    { "type": "deletion" },
    { "type": "update" }
  ]
}
EOF
)"

# Validate the payloads before any network call so a typo cannot half-apply.
python3 - "$protection_payload" "$tag_ruleset_payload" <<'PY'
import json
import sys

for name, payload in (("branch protection", sys.argv[1]), ("tag ruleset", sys.argv[2])):
    try:
        json.loads(payload)
    except json.JSONDecodeError as error:
        raise SystemExit(f"{name} payload is not valid JSON: {error}") from error
PY

if [ "$DRY_RUN" = "true" ]; then
    printf 'Repository: %s\nBranch: %s\n\n--- PUT /repos/%s/branches/%s/protection ---\n%s\n\n--- PUT/POST /repos/%s/rulesets ---\n%s\n' \
        "$REPO" "$BRANCH" "$REPO" "$BRANCH" "$protection_payload" "$REPO" "$tag_ruleset_payload"
    exit 0
fi

command -v gh >/dev/null 2>&1 || die "gh is required; install it or use --dry-run and apply the payloads by hand"
if [ -z "${GH_TOKEN:-}" ] && ! gh auth status >/dev/null 2>&1; then
    die "gh is not authenticated; set GH_TOKEN or run 'gh auth login'"
fi

printf 'Applying branch protection to %s (%s)\n' "$REPO" "$BRANCH"
printf '%s' "$protection_payload" \
    | gh api --method PUT "/repos/$REPO/branches/$BRANCH/protection" --input - >/dev/null
printf 'OK: branch protection applied\n'

# The tag ruleset is a separate API. Reuse an existing ruleset with the same
# name instead of failing on a duplicate.
existing_ruleset_id="$(gh api "/repos/$REPO/rulesets" --jq '.[] | select(.name == "protect-release-tags") | .id' 2>/dev/null || true)"
if [ -n "$existing_ruleset_id" ]; then
    printf 'Updating existing tag ruleset %s\n' "$existing_ruleset_id"
    printf '%s' "$tag_ruleset_payload" \
        | gh api --method PUT "/repos/$REPO/rulesets/$existing_ruleset_id" --input - >/dev/null
else
    printf 'Creating the release-tag ruleset\n'
    printf '%s' "$tag_ruleset_payload" \
        | gh api --method POST "/repos/$REPO/rulesets" --input - >/dev/null
fi
printf 'OK: release-tag ruleset applied\n'

printf '\nVerifying:\n'
gh api "/repos/$REPO/branches/$BRANCH/protection" \
    --jq '{required_checks: .required_status_checks.contexts, strict: .required_status_checks.strict, reviews: .required_pull_request_reviews.required_approving_review_count, conversation_resolution: .required_conversation_resolution.enabled, force_pushes: .allow_force_pushes.enabled, deletions: .allow_deletions.enabled}'
gh api "/repos/$REPO/rulesets" \
    --jq '.[] | select(.name == "protect-release-tags") | {name, enforcement, target}'

printf '\nPASS: repository settings applied. Verify the five check names in the GitHub UI if a workflow job was renamed.\n'
