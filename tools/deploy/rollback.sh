#!/usr/bin/env bash
#
# rollback.sh — move an environment back to the previous Container App revision.
#
# Usage:
#   tools/deploy/rollback.sh --environment NAME --resource-group RG [--app APP]...
#
#   --environment NAME    test | uat | prod (used in the app name)
#   --resource-group RG   resource group that contains the apps
#   --app APP             app suffix (web, auth, api, sms-mock, issuer-mock) or a
#                         full app name. Repeatable. Defaults to all five apps.
#   --name-prefix PREFIX  resource-name prefix; default "smedemo"
#   -h, --help            show this help and exit (works without az)
#
# For each app this lists revisions by creation time, activates the one before
# the newest, and moves 100% of ingress traffic to it. It prints the revision
# that is serving traffic when it finishes and exits non-zero if an app has no
# previous revision (a first deployment has nothing to roll back to).
#
# IMPORTANT: database migrations are forward-only (ADR-010). There is no down
# migration, so a rollback reverts the application only; the database stays on
# the newer schema. The previous revision must still be schema-compatible
# (expand/contract migrations are the release rule). If it is not, roll forward
# with a fixed image instead of rolling back. Auth signing keys stay on the
# mounted Azure Files share, so rolling auth back does not invalidate tokens.
#
# Requires: az (logged in), bash. No secrets are read or printed.

set -euo pipefail

DEFAULT_APPS=(web auth api sms-mock issuer-mock)

usage() {
    cat <<'EOF'
rollback.sh — move an environment back to the previous Container App revision.

Usage:
  tools/deploy/rollback.sh --environment NAME --resource-group RG [--app APP]...

  --environment NAME    test | uat | prod
  --resource-group RG   resource group that contains the apps
  --app APP             app suffix or full app name; repeatable; default all
  --name-prefix PREFIX  resource-name prefix; default "smedemo"
  -h, --help            show this help and exit (works without az)

Migrations are forward-only (ADR-010): rollback reverts the application, not
the database. The previous revision must still be schema-compatible.
EOF
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

log() {
    printf '%s\n' "$*" >&2
}

ENVIRONMENT_NAME=""
RESOURCE_GROUP=""
NAME_PREFIX="smedemo"
requested_apps=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --environment)
            [ "$#" -ge 2 ] || die "--environment requires a value"
            ENVIRONMENT_NAME="$2"
            shift 2
            ;;
        --resource-group)
            [ "$#" -ge 2 ] || die "--resource-group requires a value"
            RESOURCE_GROUP="$2"
            shift 2
            ;;
        --app)
            [ "$#" -ge 2 ] || die "--app requires a value"
            requested_apps+=("$2")
            shift 2
            ;;
        --name-prefix)
            [ "$#" -ge 2 ] || die "--name-prefix requires a value"
            NAME_PREFIX="$2"
            shift 2
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

[ -n "$ENVIRONMENT_NAME" ] || die "--environment is required"
[ -n "$RESOURCE_GROUP" ] || die "--resource-group is required"
case "$ENVIRONMENT_NAME" in
    test|uat|prod) ;;
    *) die "--environment must be test, uat or prod (got '$ENVIRONMENT_NAME')" ;;
esac

command -v az >/dev/null 2>&1 || die "Azure CLI (az) is required but was not found on PATH"

# Default to every app; otherwise keep the caller's order and de-duplicate.
if [ "${#requested_apps[@]}" -eq 0 ]; then
    requested_apps=("${DEFAULT_APPS[@]}")
fi

app_names=()
for requested in "${requested_apps[@]}"; do
    case "$requested" in
        "${NAME_PREFIX}-${ENVIRONMENT_NAME}-"*)
            app_name="$requested"
            ;;
        *)
            case "$requested" in
                web|auth|api|sms-mock|issuer-mock) ;;
                *) die "--app must be one of ${DEFAULT_APPS[*]}, or a full app name prefixed '$NAME_PREFIX-$ENVIRONMENT_NAME-' (got '$requested')" ;;
            esac
            app_name="${NAME_PREFIX}-${ENVIRONMENT_NAME}-${requested}"
            ;;
    esac

    duplicate=false
    for existing in "${app_names[@]:-}"; do
        if [ "$existing" = "$app_name" ]; then
            duplicate=true
            break
        fi
    done
    if [ "$duplicate" = false ]; then
        app_names+=("$app_name")
    fi
done

failures=0

rollback_app() {
    local app="$1"
    local revisions_output revisions previous serving

    log "Listing revisions for $app"
    if ! revisions_output="$(az containerapp revision list \
        --name "$app" \
        --resource-group "$RESOURCE_GROUP" \
        --query 'sort_by(@, &properties.createdTime)[].name' \
        -o tsv)"; then
        printf 'FAIL: could not list revisions for %s\n' "$app" >&2
        failures=$((failures + 1))
        return 1
    fi

    mapfile -t revisions <<< "$revisions_output"
    if [ "${#revisions[@]}" -lt 2 ] || [ -z "${revisions[0]}" ]; then
        printf 'FAIL: %s has no previous revision to roll back to (only %d revision(s))\n' \
            "$app" "${#revisions[@]}" >&2
        failures=$((failures + 1))
        return 1
    fi

    # Newest is the last entry; the previous revision is the one before it.
    previous="${revisions[${#revisions[@]}-2]}"
    log "Rolling $app back from ${revisions[${#revisions[@]}-1]} to $previous"

    # In multiple-revision mode the new revision receives traffic via the
    # 'latest revision' rule. Activate the old revision first so it can serve,
    # then replace the traffic rule with an explicit 100% weight.
    if ! az containerapp revision activate \
        --name "$app" \
        --resource-group "$RESOURCE_GROUP" \
        --revision "$previous" >/dev/null; then
        printf 'FAIL: could not activate revision %s of %s\n' "$previous" "$app" >&2
        failures=$((failures + 1))
        return 1
    fi

    if ! az containerapp ingress traffic set \
        --name "$app" \
        --resource-group "$RESOURCE_GROUP" \
        --revision-weight "$previous=100" >/dev/null; then
        printf 'FAIL: could not move traffic to revision %s of %s\n' "$previous" "$app" >&2
        failures=$((failures + 1))
        return 1
    fi

    serving="$(az containerapp ingress traffic show \
        --name "$app" \
        --resource-group "$RESOURCE_GROUP" \
        --query '[?weight==`100`].revisionName | [0]' \
        -o tsv 2>/dev/null || true)"
    [ -n "$serving" ] || serving="$previous"

    printf 'ROLLED BACK: %s now serves revision %s (was %s)\n' \
        "$app" "$serving" "${revisions[${#revisions[@]}-1]}"
    printf '  Database migrations are forward-only (ADR-010); the schema was not reverted. ' >&2
    printf 'Confirm the previous revision is schema-compatible.\n' >&2
}

for app_name in "${app_names[@]}"; do
    rollback_app "$app_name" || true
done

if [ "$failures" -ne 0 ]; then
    printf 'FAIL: %d app(s) could not be rolled back\n' "$failures" >&2
    exit 1
fi

printf 'PASS: rollback completed for %d app(s)\n' "${#app_names[@]}"
