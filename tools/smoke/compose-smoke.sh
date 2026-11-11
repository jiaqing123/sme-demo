#!/usr/bin/env bash
#
# End-to-end smoke test for the Compose stack.
#
# Run it after `docker compose up -d` (the minimal set in the CI compose-smoke
# job) once auth and api are healthy:
#
#   bash tools/smoke/compose-smoke.sh
#
# It logs in with the demo credentials, completes the OTP challenge, calls
# GET /api/v1/me with the access token, and verifies the unauthenticated path.
# Every check prints PASS or FAIL; any failure makes the script exit non-zero.
#
# Only curl and python3 are required. python3 is used to read the small JSON
# responses so the checks do not depend on jq or brittle grep/sed parsing.

set -uo pipefail

AUTH_BASE_URL="${AUTH_BASE_URL:-http://localhost:8081}"
API_BASE_URL="${API_BASE_URL:-http://localhost:8082}"
DEMO_USERNAME="${DEMO_USERNAME:-demo}"
DEMO_PASSWORD="${DEMO_PASSWORD:-Demo_Passw0rd!}"
DEMO_OTP="${DEMO_OTP:-123456}"
EXPECTED_ORG="${ORG_ID:-sme}"

HEALTH_ATTEMPTS="${HEALTH_ATTEMPTS:-90}"
HEALTH_INTERVAL_SECONDS="${HEALTH_INTERVAL_SECONDS:-2}"

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
# Prints the value, or an empty string when the file is not JSON or the path is
# missing. Arrays may be indexed with a numeric path segment.
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

# request <method> <url> <bearer-token-or-empty> <json-body-or-empty> <outfile>
# Prints the HTTP status code, or an empty string when the request failed.
request() {
    local method="$1"
    local url="$2"
    local token="$3"
    local body="$4"
    local outfile="$5"
    local args=(-sS --max-time 15 -o "$outfile" -w '%{http_code}' -X "$method" "$url")

    if [ -n "$token" ]; then
        args+=(-H "Authorization: Bearer $token")
    fi

    if [ -n "$body" ]; then
        args+=(-H 'Content-Type: application/json' --data "$body")
    fi

    curl "${args[@]}" 2>/dev/null || true
}

wait_for_health() {
    local name="$1"
    local base_url="$2"
    local attempt code

    for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
        code="$(curl -sS --max-time 3 -o /dev/null -w '%{http_code}' \
            "$base_url/healthz" 2>/dev/null || true)"
        if [ "$code" = "200" ]; then
            pass "$name is healthy at $base_url/healthz (attempt $attempt)"
            return 0
        fi
        sleep "$HEALTH_INTERVAL_SECONDS"
    done

    fail "$name did not return 200 from $base_url/healthz after $HEALTH_ATTEMPTS attempts"
    return 1
}

printf 'Compose smoke test: auth=%s api=%s org=%s\n' \
    "$AUTH_BASE_URL" "$API_BASE_URL" "$EXPECTED_ORG"

wait_for_health "auth" "$AUTH_BASE_URL"
wait_for_health "api" "$API_BASE_URL"

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

login_status="$(request POST "$AUTH_BASE_URL/auth/login" "" "$login_body" "$tmp_dir/login.json")"
challenge_id="$(json_field "$tmp_dir/login.json" 'challenge_id')"

if [ "$login_status" = "200" ] && [ -n "$challenge_id" ]; then
    pass "POST /auth/login returned 200 with a challenge_id"
else
    fail "POST /auth/login did not return a challenge (status=${login_status:-<no response>})"
    cat "$tmp_dir/login.json" 2>/dev/null || true
    printf '\n'
fi

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
    verify_status="$(request POST \
        "$AUTH_BASE_URL/auth/challenges/$challenge_id/verify" \
        "" "$verify_body" "$tmp_dir/verify.json")"
    access_token="$(json_field "$tmp_dir/verify.json" 'access_token')"

    if [ "$verify_status" = "200" ] && [ -n "$access_token" ]; then
        pass "POST /auth/challenges/<id>/verify returned 200 with an access_token"
    else
        fail "OTP verification did not return an access token (status=${verify_status:-<no response>})"
        cat "$tmp_dir/verify.json" 2>/dev/null || true
        printf '\n'
    fi
fi

if [ -z "$access_token" ]; then
    fail "cannot call GET /api/v1/me without an access token"
else
    me_status="$(request GET "$API_BASE_URL/api/v1/me" "$access_token" "" "$tmp_dir/me.json")"
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

anonymous_status="$(request GET "$API_BASE_URL/api/v1/me" "" "" "$tmp_dir/anonymous.json")"
anonymous_code="$(json_field "$tmp_dir/anonymous.json" 'error.code')"

if [ "$anonymous_status" = "401" ] && [ "$anonymous_code" = "UNAUTHENTICATED" ]; then
    pass "GET /api/v1/me without a token returned 401 with error.code=UNAUTHENTICATED"
else
    fail "unauthenticated GET /api/v1/me was not the documented 401 (status=${anonymous_status:-<no response>} code=${anonymous_code:-<empty>})"
    cat "$tmp_dir/anonymous.json" 2>/dev/null || true
    printf '\n'
fi

if [ "$failures" -ne 0 ]; then
    printf 'FAIL: %d compose smoke check(s) failed\n' "$failures" >&2
    exit 1
fi

printf 'PASS: all compose smoke checks passed\n'
