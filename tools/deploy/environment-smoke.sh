#!/usr/bin/env bash
#
# environment-smoke.sh — public user journey against a deployed environment.
#
# Usage:
#   WEB_BASE_URL=https://<web-fqdn> tools/deploy/environment-smoke.sh
#
# Optional environment:
#   DEMO_USERNAME   default "demo"
#   DEMO_PASSWORD   default "Demo_Passw0rd!"
#   DEMO_OTP        default "123456"
#   ORG_ID          expected organisation, default "sme"
#   WEB_WAIT_ATTEMPTS / WEB_WAIT_INTERVAL_SECONDS  startup tolerance, default 30 x 2s
#
#   -h, --help      show this help and exit
#
# The script follows the same public contract as tools/smoke/compose-smoke.sh,
# but talks only to the web origin (nginx proxies /auth and /api), because the
# auth and api apps use internal ingress and are not reachable from the
# pipeline agent. Dependency readiness is enforced by the platform probes
# (api readiness is GET /readyz; auth and the mocks use GET /healthz), not by
# this script. This script proves the path a browser actually uses.
#
# Prints one PASS/FAIL line per check and exits non-zero if any check fails.
# Requires: curl, python3. No jq.

set -uo pipefail

usage() {
    cat <<'EOF'
environment-smoke.sh — public user journey against a deployed environment.

Usage:
  WEB_BASE_URL=https://<web-fqdn> tools/deploy/environment-smoke.sh

Environment: WEB_BASE_URL (required), DEMO_USERNAME, DEMO_PASSWORD, DEMO_OTP,
ORG_ID, WEB_WAIT_ATTEMPTS, WEB_WAIT_INTERVAL_SECONDS.
EOF
}

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    usage
    exit 0
fi
[ "$#" -eq 0 ] || { printf 'ERROR: unexpected arguments: %s\n' "$*" >&2; exit 1; }

WEB_BASE_URL="${WEB_BASE_URL:-}"
if [ -z "$WEB_BASE_URL" ]; then
    printf 'ERROR: WEB_BASE_URL is required (for example https://smedemo-prod-web.<domain>)\n' >&2
    exit 1
fi
WEB_BASE_URL="${WEB_BASE_URL%/}"

DEMO_USERNAME="${DEMO_USERNAME:-demo}"
DEMO_PASSWORD="${DEMO_PASSWORD:-Demo_Passw0rd!}"
DEMO_OTP="${DEMO_OTP:-123456}"
EXPECTED_ORG="${ORG_ID:-sme}"

WEB_WAIT_ATTEMPTS="${WEB_WAIT_ATTEMPTS:-30}"
WEB_WAIT_INTERVAL_SECONDS="${WEB_WAIT_INTERVAL_SECONDS:-2}"

failures=0
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

pass() {
    printf 'PASS: %s\n' "$*"
}

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    failures=$((failures + 1))
}

# json_field <file> <dotted.path>
# Prints the value or an empty string when the file is not JSON or the path is
# missing. Numeric segments index arrays.
json_field() {
    python3 - "$1" "$2" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        value = json.load(handle)
    for part in sys.argv[2].split("."):
        value = value[int(part)] if isinstance(value, list) else value[part]
except Exception:
    value = ""

if value is None:
    value = ""
print(value)
PY
}

# request <method> <path> <bearer-token-or-empty> <json-body-or-empty> <outfile> [extra curl args...]
# Prints the HTTP status code, or an empty string when the request failed.
request() {
    local method="$1"
    local path="$2"
    local token="$3"
    local body="$4"
    local outfile="$5"
    shift 5
    local args=(-sS --max-time 20 -o "$outfile" -w '%{http_code}' -X "$method" "$WEB_BASE_URL$path" "$@")

    if [ -n "$token" ]; then
        args+=(-H "Authorization: Bearer $token")
    fi
    if [ -n "$body" ]; then
        args+=(-H 'Content-Type: application/json' --data "$body")
    fi

    curl "${args[@]}" 2>/dev/null || true
}

printf 'Environment smoke test: origin=%s org=%s\n' "$WEB_BASE_URL" "$EXPECTED_ORG"

# Azure Container Apps assigns a new public hostname after the revision is
# ready, and the platform DNS answer can take a moment to appear. Wait for the
# public origin before running the functional checks; this is not a dependency
# probe (the platform probes own that).
web_code=""
for ((attempt = 1; attempt <= WEB_WAIT_ATTEMPTS; attempt++)); do
    web_code="$(curl -sS --max-time 10 -o /dev/null -w '%{http_code}' "$WEB_BASE_URL/" 2>/dev/null || true)"
    if [ "$web_code" = "200" ]; then
        break
    fi
    sleep "$WEB_WAIT_INTERVAL_SECONDS"
done

# 1. The SPA shell is served and is HTML.
root_status="$(request GET "/" "" "" "$tmp_dir/root.html")"
if [ "$root_status" = "200" ] && grep -Eiq '<!doctype html|<html' "$tmp_dir/root.html"; then
    pass "GET / returned 200 HTML"
else
    fail "GET / did not return 200 HTML (status=${root_status:-<no response>})"
fi

# 2. /config.js is generated per start and must never be cached: a stale demo
# hint would point at a credential that no longer exists.
config_status="$(request GET "/config.js" "" "" "$tmp_dir/config.js" -D "$tmp_dir/config.headers")"
if [ "$config_status" = "200" ] && grep -iq '^cache-control:.*no-store' "$tmp_dir/config.headers"; then
    pass "GET /config.js returned 200 with Cache-Control: no-store"
else
    fail "GET /config.js was not 200 + no-store (status=${config_status:-<no response>})"
fi

# 3. Login with the published demo credentials returns an OTP challenge.
login_body="$(
    DEMO_USERNAME="$DEMO_USERNAME" DEMO_PASSWORD="$DEMO_PASSWORD" python3 - <<'PY'
import json
import os

print(json.dumps({
    "username": os.environ["DEMO_USERNAME"],
    "password": os.environ["DEMO_PASSWORD"],
}))
PY
)"
login_status="$(request POST "/auth/login" "" "$login_body" "$tmp_dir/login.json")"
challenge_id="$(json_field "$tmp_dir/login.json" 'challenge_id')"

if [ "$login_status" = "200" ] && [ -n "$challenge_id" ]; then
    pass "POST /auth/login returned 200 with a challenge_id"
else
    fail "POST /auth/login did not return a challenge (status=${login_status:-<no response>})"
    cat "$tmp_dir/login.json" 2>/dev/null || true
    printf '\n'
fi

# 4. The fixed demo OTP completes the challenge and returns an access token.
access_token=""
if [ -z "$challenge_id" ]; then
    fail "cannot verify the OTP challenge without a challenge_id"
else
    verify_body="$(
        DEMO_OTP="$DEMO_OTP" python3 - <<'PY'
import json
import os

print(json.dumps({"code": os.environ["DEMO_OTP"]}))
PY
    )"
    verify_status="$(request POST "/auth/challenges/$challenge_id/verify" "" "$verify_body" "$tmp_dir/verify.json")"
    access_token="$(json_field "$tmp_dir/verify.json" 'access_token')"

    if [ "$verify_status" = "200" ] && [ -n "$access_token" ]; then
        pass "POST /auth/challenges/<id>/verify returned 200 with an access_token"
    else
        fail "OTP verification did not return an access token (status=${verify_status:-<no response>})"
        cat "$tmp_dir/verify.json" 2>/dev/null || true
        printf '\n'
    fi
fi

# 5. The token authorises the API through the same web origin.
if [ -z "$access_token" ]; then
    fail "cannot call GET /api/v1/me without an access token"
else
    me_status="$(request GET "/api/v1/me" "$access_token" "" "$tmp_dir/me.json")"
    me_org="$(json_field "$tmp_dir/me.json" 'org')"
    me_name="$(json_field "$tmp_dir/me.json" 'name')"

    if [ "$me_status" = "200" ] && [ "$me_org" = "$EXPECTED_ORG" ] && [ -n "$me_name" ]; then
        pass "GET /api/v1/me returned 200 with org=$EXPECTED_ORG and a non-empty name"
    else
        fail "GET /api/v1/me failed (status=${me_status:-<no response>} org=${me_org:-<empty>} name=${me_name:-<empty>})"
        cat "$tmp_dir/me.json" 2>/dev/null || true
        printf '\n'
    fi
fi

# 6. The unauthenticated path is the documented 401, not a redirect to login.
anonymous_status="$(request GET "/api/v1/me" "" "" "$tmp_dir/anonymous.json")"
anonymous_code="$(json_field "$tmp_dir/anonymous.json" 'error.code')"

if [ "$anonymous_status" = "401" ] && [ "$anonymous_code" = "UNAUTHENTICATED" ]; then
    pass "GET /api/v1/me without a token returned 401 with error.code=UNAUTHENTICATED"
else
    fail "unauthenticated GET /api/v1/me was not the documented 401 (status=${anonymous_status:-<no response>} code=${anonymous_code:-<empty>})"
    cat "$tmp_dir/anonymous.json" 2>/dev/null || true
    printf '\n'
fi

if [ "$failures" -ne 0 ]; then
    printf 'FAIL: %d environment smoke check(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'PASS: all environment smoke checks passed\n'
