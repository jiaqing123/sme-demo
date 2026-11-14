#!/usr/bin/env bash
#
# deploy.sh — deploy one already-built digest set to one environment.
#
# The pipeline calls this from an Ubuntu agent after resolve-digests.sh. The
# script applies infra/main.bicep, runs the three one-shot jobs in dependency
# order, waits for the new revisions, records the deployment, and finally runs
# the public smoke test.
#
# Usage:
#   tools/deploy/deploy.sh
#
# Required environment:
#   ENVIRONMENT_NAME      test | uat | prod
#   RESOURCE_GROUP        target resource group (must also contain the ACR, see notes)
#   LOCATION              Azure region for new resources
#   ACR_NAME              existing Azure Container Registry name
#   IMAGES_JSON           path to resolve-digests.sh output, or the JSON itself
#   SQL_ADMIN_LOGIN       SQL Server administrator login
#   SQL_ADMIN_PASSWORD    SQL Server administrator password
#   SME_API_DB_PASSWORD   password for sme_api_app
#   SME_AUTH_DB_PASSWORD  password for sme_auth_app
#   ORG_ID                tenant organisation id, usually "sme"
#   DEMO_MODE             true | false
#   DEMO_USERNAME         published demo username
#   DEMO_PASSWORD         published demo password
#   DEMO_OTP              published fixed demo OTP
#   ISSUER_API_KEY        shared key for issuer-mock
#
# Optional environment:
#   NAME_PREFIX             resource-name prefix; default "smedemo"
#   DEPLOYMENT_NAME         ARM deployment name; default smedemo-<env>-<UTC timestamp>
#   SOURCE_COMMIT           commit recorded in the deployment record (no secret)
#   BOOTSTRAP_SQL_PATH      bootstrap SQL path in the migrator image
#                           (default /app/bootstrap/azure-app-users.sql)
#   DEPLOYMENT_RECORD_FILE  also write the deployment record to this file
#   SKIP_SMOKE              "true" to skip the public smoke test
#
#   -h, --help              show this help and exit (works without az)
#
# Requires: az (logged in to the target subscription), python3, bash.
# No jq. Never prints a password or connection string.
#
# SQL_ADMIN_PASSWORD, SME_API_DB_PASSWORD and SME_AUTH_DB_PASSWORD are validated
# against a safe alphabet: no quote, semicolon, backslash, backtick or
# whitespace. They are substituted into a T-SQL literal and a connection string,
# so those characters would break the deployment.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

usage() {
    cat <<'EOF'
deploy.sh — deploy one already-built digest set to one environment.

Required: ENVIRONMENT_NAME RESOURCE_GROUP LOCATION ACR_NAME IMAGES_JSON
          SQL_ADMIN_LOGIN SQL_ADMIN_PASSWORD SME_API_DB_PASSWORD
          SME_AUTH_DB_PASSWORD ORG_ID DEMO_MODE DEMO_USERNAME DEMO_PASSWORD
          DEMO_OTP ISSUER_API_KEY
Optional: NAME_PREFIX DEPLOYMENT_NAME SOURCE_COMMIT BOOTSTRAP_SQL_PATH
          DEPLOYMENT_RECORD_FILE SKIP_SMOKE

See the header of this file for the exact meaning of each variable.
EOF
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

log() {
    printf '%s\n' "$*" >&2
}

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    usage
    exit 0
fi
[ "$#" -eq 0 ] || die "unexpected arguments: $* (this script is driven by environment variables)"

# ---------------------------------------------------------------------------
# Input validation
# ---------------------------------------------------------------------------

required_vars=(
    ENVIRONMENT_NAME
    RESOURCE_GROUP
    LOCATION
    ACR_NAME
    IMAGES_JSON
    SQL_ADMIN_LOGIN
    SQL_ADMIN_PASSWORD
    SME_API_DB_PASSWORD
    SME_AUTH_DB_PASSWORD
    ORG_ID
    DEMO_MODE
    DEMO_USERNAME
    DEMO_PASSWORD
    DEMO_OTP
    ISSUER_API_KEY
)

for name in "${required_vars[@]}"; do
    if [ -z "${!name:-}" ]; then
        die "$name is required"
    fi
done

case "$ENVIRONMENT_NAME" in
    test|uat|prod) ;;
    *) die "ENVIRONMENT_NAME must be test, uat or prod (got '$ENVIRONMENT_NAME')" ;;
esac

case "$DEMO_MODE" in
    true|false) ;;
    *) die "DEMO_MODE must be true or false (got '$DEMO_MODE')" ;;
esac

# The three passwords are substituted raw into a T-SQL string literal (bootstrap)
# and into a SqlClient connection string. A quote breaks the SQL literal and a
# semicolon corrupts the connection string, so reject the characters that cannot
# be represented safely by this simple mechanism instead of silently producing a
# broken deployment. The operators' secret store must generate passwords from a
# safe alphabet (the Azure SQL complexity rule is satisfied without these).
for password_var in SQL_ADMIN_PASSWORD SME_API_DB_PASSWORD SME_AUTH_DB_PASSWORD; do
    password_value="${!password_var}"
    case "$password_value" in
        *"'"*|*';'*|*'"'*|*'\'*|*'`'*|*[[:space:]]*)
            die "$password_var contains a quote, semicolon, backslash, backtick or whitespace; generate it from a safe alphabet"
            ;;
    esac
done

NAME_PREFIX="${NAME_PREFIX:-smedemo}"
BOOTSTRAP_SQL_PATH="${BOOTSTRAP_SQL_PATH:-/app/bootstrap/azure-app-users.sql}"
SKIP_SMOKE="${SKIP_SMOKE:-false}"
SOURCE_COMMIT="${SOURCE_COMMIT:-}"
DEPLOYMENT_NAME="${DEPLOYMENT_NAME:-smedemo-$ENVIRONMENT_NAME-$(date -u +%Y%m%d%H%M%S)}"

case "$SKIP_SMOKE" in
    true|false) ;;
    *) die "SKIP_SMOKE must be true or false (got '$SKIP_SMOKE')" ;;
esac

[ -f "$REPO_ROOT/infra/main.bicep" ] || die "infra/main.bicep not found under $REPO_ROOT"
command -v az >/dev/null 2>&1 || die "Azure CLI (az) is required but was not found on PATH"
command -v python3 >/dev/null 2>&1 || die "python3 is required but was not found on PATH"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

# IMAGES_JSON is normally a file written by resolve-digests.sh, but accepting
# an inline document makes a manual rerun from a pipeline log possible.
if [ -f "$IMAGES_JSON" ]; then
    images_file="$IMAGES_JSON"
else
    images_file="$tmp_dir/images.json"
    printf '%s' "$IMAGES_JSON" > "$images_file"
fi

json_get() {
    python3 - "$images_file" "$1" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    value = json.load(handle)
for part in sys.argv[2].split("."):
    value = value[part]
print(value)
PY
}

image_ref() {
    json_get "images.$1"
}

REGISTRY="$(json_get registry)"
IMAGE_TAG="$(json_get tag)"
[ -n "$REGISTRY" ] || die "IMAGES_JSON has no registry"
[ -n "$IMAGE_TAG" ] || die "IMAGES_JSON has no tag"

for image_name in api auth web sms-mock issuer-mock migrator; do
    ref="$(image_ref "$image_name")"
    [ -n "$ref" ] || die "IMAGES_JSON has no images.$image_name"
    case "$ref" in
        *"@sha256:"*) ;;
        *) die "images.$image_name is not pinned by digest: $ref" ;;
    esac
    digest="${ref##*@}"
    [ "${#digest}" -eq 71 ] || die "images.$image_name has a malformed digest: $digest"
    case "${digest#sha256:}" in
        *[!0-9a-f]*) die "images.$image_name has a malformed digest: $digest" ;;
    esac
done

# The single-file template creates the AcrPull role assignment at the registry
# scope, which Bicep can only do inside the deployment resource group (a
# cross-resource-group assignment would need a module). Fail early with a clear
# explanation instead of letting the deployment fail halfway through.
if ! acr_login_server="$(az acr show --name "$ACR_NAME" --query loginServer -o tsv)"; then
    die "could not read ACR $ACR_NAME (is the Azure CLI logged in to the right subscription?)"
fi
if ! acr_resource_group="$(az acr show --name "$ACR_NAME" --query resourceGroup -o tsv)"; then
    die "could not read the resource group of ACR $ACR_NAME"
fi
[ "$acr_resource_group" = "$RESOURCE_GROUP" ] || die \
    "ACR $ACR_NAME is in resource group '$acr_resource_group', but the deployment targets '$RESOURCE_GROUP'. " \
    "The single-file Bicep template can only create the AcrPull role assignment when both are the same group."

case "$(image_ref api)" in
    "$acr_login_server"/*) ;;
    *) die "api image is not from $acr_login_server; refusing to deploy an unexpected registry" ;;
esac

# ---------------------------------------------------------------------------
# Template deployment
# ---------------------------------------------------------------------------
# The template parameters mirror infra/main.bicep. Secrets are passed as
# key=value pairs because the template consumes secure parameters directly:
# Azure DevOps masks registered secret variables in its own log, and the agent
# process command line is visible only to that agent's user. The alternative,
# for a stricter environment, is a generated --parameters @file whose values
# come from a Key Vault-linked variable group; the file must then be deleted
# immediately (this script already uses a private mktemp directory for
# non-secret intermediates).

deployment_params=(
    "environmentName=$ENVIRONMENT_NAME"
    "location=$LOCATION"
    "namePrefix=$NAME_PREFIX"
    "acrName=$ACR_NAME"
    "acrResourceGroup=$RESOURCE_GROUP"
    "apiImage=$(image_ref api)"
    "authImage=$(image_ref auth)"
    "webImage=$(image_ref web)"
    "smsMockImage=$(image_ref sms-mock)"
    "issuerMockImage=$(image_ref issuer-mock)"
    "migratorImage=$(image_ref migrator)"
    "sqlAdminLogin=$SQL_ADMIN_LOGIN"
    "sqlAdminPassword=$SQL_ADMIN_PASSWORD"
    "apiDbPassword=$SME_API_DB_PASSWORD"
    "authDbPassword=$SME_AUTH_DB_PASSWORD"
    "issuerApiKey=$ISSUER_API_KEY"
    "demoMode=$DEMO_MODE"
    "orgId=$ORG_ID"
    "demoUsername=$DEMO_USERNAME"
    "demoPassword=$DEMO_PASSWORD"
    "demoOtp=$DEMO_OTP"
    "bootstrapSqlPath=$BOOTSTRAP_SQL_PATH"
)

log "Deploying $ENVIRONMENT_NAME to resource group $RESOURCE_GROUP (deployment $DEPLOYMENT_NAME)"
if ! az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DEPLOYMENT_NAME" \
    --template-file "$REPO_ROOT/infra/main.bicep" \
    --parameters "${deployment_params[@]}" \
    --only-show-errors \
    --output json > "$tmp_dir/deployment.json"; then
    die "ARM deployment $DEPLOYMENT_NAME failed (see the Azure portal or 'az deployment group show')"
fi

output_value() {
    python3 - "$tmp_dir/deployment.json" "$1" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    deployment = json.load(handle)
print(deployment["properties"]["outputs"][sys.argv[2]]["value"])
PY
}

web_url="$(output_value webUrl)"
web_fqdn="$(output_value webFqdn)"
api_fqdn="$(output_value apiFqdn)"
auth_fqdn="$(output_value authFqdn)"
sql_server_fqdn="$(output_value sqlServerFqdn)"

log "Deployment outputs: web=$web_url api=$api_fqdn auth=$auth_fqdn sql=$sql_server_fqdn"

# ---------------------------------------------------------------------------
# One-shot jobs
# ---------------------------------------------------------------------------
# Order matters. job-db-grants runs against master and creates the two
# server-level logins; the two migrate jobs then apply their schema and run the
# same bootstrap script against sme_api / sme_auth to create the database users
# and their reader/writer roles. The jobs use the administrator connection
# strings from Key Vault; the application logins cannot run DDL.
#
# A job that fails is not retried by the platform (replicaRetryLimit: 0). The
# operator restarts it with the same command after fixing the cause; the
# migration and bootstrap scripts themselves are idempotent.

JOB_TIMEOUT_SECONDS="${JOB_TIMEOUT_SECONDS:-900}"
JOB_POLL_SECONDS="${JOB_POLL_SECONDS:-10}"

job_log_command() {
    printf 'Inspect the execution log with:\n  az containerapp job logs show --name %s --resource-group %s --execution %s --container migrator\n' \
        "$1" "$RESOURCE_GROUP" "$2" >&2
}

latest_execution_name() {
    az containerapp job execution list \
        --name "$1" \
        --resource-group "$RESOURCE_GROUP" \
        --query 'sort_by(@, &properties.startTime)[-1].name' \
        -o tsv
}

run_job() {
    local job="$1"
    local execution status deadline

    log "Starting job $job"
    # `job start` returns the execution name in recent CLI versions, but do not
    # depend on that. A failed start is fatal: falling back to the newest
    # existing execution could silently accept a previous successful run.
    if ! start_output="$(az containerapp job start --name "$job" --resource-group "$RESOURCE_GROUP" --query name -o tsv 2>/dev/null)"; then
        die "could not start job $job (a failed start must not fall back to an older execution)"
    fi
    execution="$start_output"
    if [ -z "$execution" ] || [ "$execution" = "None" ]; then
        execution="$(latest_execution_name "$job")"
    fi
    [ -n "$execution" ] || die "job $job did not return an execution name"

    log "Job $job execution $execution started; waiting up to ${JOB_TIMEOUT_SECONDS}s"
    deadline=$((SECONDS + JOB_TIMEOUT_SECONDS))

    while :; do
        if ! status="$(az containerapp job execution show \
            --name "$job" \
            --job-execution-name "$execution" \
            --resource-group "$RESOURCE_GROUP" \
            --query properties.status -o tsv)"; then
            die "could not read the status of job $job execution $execution"
        fi

        case "$status" in
            Succeeded)
                log "Job $job succeeded"
                return 0
                ;;
            Failed|Canceled|Cancelled|Degraded)
                job_log_command "$job" "$execution"
                die "job $job execution $execution finished with status $status"
                ;;
        esac

        if [ "$SECONDS" -ge "$deadline" ]; then
            job_log_command "$job" "$execution"
            die "job $job execution $execution did not finish within ${JOB_TIMEOUT_SECONDS}s (last status: $status)"
        fi

        sleep "$JOB_POLL_SECONDS"
    done
}

# 1. Server logins on master. 2-3. Schema then database users, one database per
# job. Any non-zero job fails the deployment immediately.
run_job "${NAME_PREFIX}-${ENVIRONMENT_NAME}-job-db-grants"
run_job "${NAME_PREFIX}-${ENVIRONMENT_NAME}-job-migrate-api"
run_job "${NAME_PREFIX}-${ENVIRONMENT_NAME}-job-migrate-auth"

# ---------------------------------------------------------------------------
# Revision readiness
# ---------------------------------------------------------------------------
# Provenance revision activation is asynchronous. A revision can exist
# (latestRevisionName) before it is ready (latestReadyRevisionName), and the
# app's runningStatus must be Running. This waits for that contract; it does
# not call the internal /readyz endpoint, which is not reachable from the
# pipeline agent (internal ingress only).

REVISION_TIMEOUT_SECONDS="${REVISION_TIMEOUT_SECONDS:-600}"
REVISION_POLL_SECONDS="${REVISION_POLL_SECONDS:-10}"

wait_for_revision() {
    local app="$1"
    local status_line latest ready running deadline

    log "Waiting for the latest revision of $app to be ready"
    deadline=$((SECONDS + REVISION_TIMEOUT_SECONDS))

    while :; do
        if ! status_line="$(az containerapp show \
            --name "$app" \
            --resource-group "$RESOURCE_GROUP" \
            --query '[properties.latestRevisionName, properties.latestReadyRevisionName, properties.runningStatus]' \
            -o tsv)"; then
            die "could not read revision status for $app"
        fi

        IFS=$'\t' read -r latest ready running <<< "$status_line" || true

        if [ -n "$latest" ] && [ "$latest" = "$ready" ] && [ "$running" = "Running" ]; then
            log "$app is serving revision $latest"
            return 0
        fi

        if [ "$SECONDS" -ge "$deadline" ]; then
            printf 'Inspect revisions with:\n  az containerapp revision list --name %s --resource-group %s -o table\n' \
                "$app" "$RESOURCE_GROUP" >&2
            printf 'Inspect logs with:\n  az containerapp logs show --name %s --resource-group %s --follow\n' \
                "$app" "$RESOURCE_GROUP" >&2
            die "$app did not reach a ready, Running revision within ${REVISION_TIMEOUT_SECONDS}s (latest=$latest ready=$ready status=$running)"
        fi

        sleep "$REVISION_POLL_SECONDS"
    done
}

# The template creates the apps before the jobs exist in this run, so an app may
# have started (and crashed or backed off) before its schema was migrated.
# Restart the latest revision now that the schema and logins exist; the restart
# clears the platform backoff and gives the readiness loop a fresh attempt.
# A failed restart is not fatal: the revision may still be provisioning, and the
# readiness wait below is the real gate.
for app_suffix in web auth api sms-mock issuer-mock; do
    app="${NAME_PREFIX}-${ENVIRONMENT_NAME}-${app_suffix}"
    revision="$(az containerapp show --name "$app" --resource-group "$RESOURCE_GROUP" \
        --query properties.latestRevisionName -o tsv 2>/dev/null || true)"
    if [ -n "$revision" ]; then
        log "Restarting $app revision $revision after migrations"
        az containerapp revision restart --name "$app" --resource-group "$RESOURCE_GROUP" \
            --revision "$revision" >/dev/null 2>&1 || log "restart of $app was not needed or not accepted; continuing"
    fi
done

for app_suffix in web auth api sms-mock issuer-mock; do
    wait_for_revision "${NAME_PREFIX}-${ENVIRONMENT_NAME}-${app_suffix}"
done

# ---------------------------------------------------------------------------
# Deployment record
# ---------------------------------------------------------------------------
# No secret or connection-string value appears here. This record is what goes
# into the release notes / ADR evidence.

deployment_record="$(
    python3 - "$ENVIRONMENT_NAME" "$DEPLOYMENT_NAME" "$RESOURCE_GROUP" "$web_url" "$IMAGE_TAG" "$SOURCE_COMMIT" \
        "$(image_ref api)" "$(image_ref auth)" "$(image_ref web)" \
        "$(image_ref sms-mock)" "$(image_ref issuer-mock)" "$(image_ref migrator)" <<'PY'
import json
import sys
from datetime import datetime, timezone

environment, deployment, resource_group, web_url, tag, commit = sys.argv[1:7]
refs = dict(zip(("api", "auth", "web", "sms-mock", "issuer-mock", "migrator"), sys.argv[7:13]))

print(json.dumps({
    "environment": environment,
    "deploymentName": deployment,
    "resourceGroup": resource_group,
    "webUrl": web_url,
    "tag": tag,
    "commit": commit or None,
    "timestamp": datetime.now(timezone.utc).isoformat(),
    "images": refs,
    "digests": {name: ref.split("@", 1)[1] for name, ref in refs.items()},
}, indent=2))
PY
)"

printf '%s\n' "$deployment_record"
if [ -n "${DEPLOYMENT_RECORD_FILE:-}" ]; then
    mkdir -p "$(dirname "$DEPLOYMENT_RECORD_FILE")"
    printf '%s\n' "$deployment_record" > "$DEPLOYMENT_RECORD_FILE"
    log "Wrote deployment record to $DEPLOYMENT_RECORD_FILE"
fi

# ---------------------------------------------------------------------------
# Public smoke test
# ---------------------------------------------------------------------------

if [ "$SKIP_SMOKE" = "true" ]; then
    log "SKIP_SMOKE=true; skipping environment-smoke.sh"
    exit 0
fi

log "Running the public smoke test against $web_url"
export WEB_BASE_URL="$web_url"
export DEMO_USERNAME="$DEMO_USERNAME"
export DEMO_PASSWORD="$DEMO_PASSWORD"
export DEMO_OTP="$DEMO_OTP"
export ORG_ID="$ORG_ID"
exec bash "$SCRIPT_DIR/environment-smoke.sh"
