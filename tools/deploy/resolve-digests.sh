#!/usr/bin/env bash
#
# resolve-digests.sh — turn one build tag into six immutable image references.
#
# The pipeline builds the six sme-demo images once, pushes them to ACR with a
# human tag (the commit SHA), then calls this script. The resulting JSON is the
# deployment contract: everything downstream promotes the digest, never a tag.
#
# Usage:
#   tools/deploy/resolve-digests.sh --acr NAME --tag TAG --output FILE
#
#   --acr NAME      Existing Azure Container Registry (not the login server).
#   --tag TAG       Build tag to resolve, e.g. the commit SHA.
#   --output FILE   Write the JSON document here as well as to stdout.
#   -h, --help      Show this help and exit (works without az installed).
#
# Output (stdout and FILE):
#   {
#     "registry": "myacr.azurecr.io",
#     "tag": "abc1234",
#     "images": {
#       "api": "myacr.azurecr.io/sme-demo/api@sha256:...",
#       ...
#     }
#   }
#
# Requires: az (logged in), python3. No jq, no secrets in the output.

set -euo pipefail

readonly REPOS=(api auth web sms-mock issuer-mock migrator)

usage() {
    cat <<'EOF'
resolve-digests.sh — turn one build tag into six immutable image references.

Usage:
  tools/deploy/resolve-digests.sh --acr NAME --tag TAG --output FILE

  --acr NAME      Existing Azure Container Registry (not the login server).
  --tag TAG       Build tag to resolve, e.g. the commit SHA.
  --output FILE   Write the JSON document here as well as to stdout.
  -h, --help      Show this help and exit (works without az installed).

Output (stdout and FILE):
  {"registry":"myacr.azurecr.io","tag":"abc1234",
   "images":{"api":"myacr.azurecr.io/sme-demo/api@sha256:...", ...}}

Requires: az (logged in), python3. No jq, no secrets in the output.
EOF
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

ACR=""
TAG=""
OUTPUT=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        --acr)
            [ "$#" -ge 2 ] || die "--acr requires a value"
            ACR="$2"
            shift 2
            ;;
        --tag)
            [ "$#" -ge 2 ] || die "--tag requires a value"
            TAG="$2"
            shift 2
            ;;
        --output)
            [ "$#" -ge 2 ] || die "--output requires a value"
            OUTPUT="$2"
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

[ -n "$ACR" ] || die "--acr is required"
[ -n "$TAG" ] || die "--tag is required"
[ -n "$OUTPUT" ] || die "--output is required"

# Keep the JMESPath literal safe and reject tags that cannot be a literal.
case "$TAG" in
    *[!A-Za-z0-9._-]*)
        die "--tag contains characters that are not valid in an ACR tag: $TAG"
        ;;
esac

command -v az >/dev/null 2>&1 || die "Azure CLI (az) is required but was not found on PATH"
command -v python3 >/dev/null 2>&1 || die "python3 is required but was not found on PATH"

printf 'Resolving tag %s in ACR %s\n' "$TAG" "$ACR" >&2
REGISTRY="$(az acr show --name "$ACR" --query loginServer -o tsv)"
[ -n "$REGISTRY" ] || die "could not read loginServer for ACR $ACR"

image_pairs=()
for repo in "${REPOS[@]}"; do
    digest="$(az acr repository show-manifests \
        --name "$ACR" \
        --repository "sme-demo/$repo" \
        --query "[?tags[?@=='$TAG']].digest | [0]" \
        -o tsv)"

    if [ -z "$digest" ] || [ "$digest" = "None" ]; then
        die "no manifest tagged '$TAG' found in sme-demo/$repo (was the Build stage published?)"
    fi

    image_pairs+=("${repo}=${REGISTRY}/sme-demo/${repo}@${digest}")
    printf '  %-12s %s\n' "$repo" "$digest" >&2
done

mkdir -p "$(dirname "$OUTPUT")"

json_payload="$(
    python3 - "$REGISTRY" "$TAG" "${image_pairs[@]}" <<'PY'
import json
import sys

registry, tag = sys.argv[1], sys.argv[2]
images = {}
for pair in sys.argv[3:]:
    name, reference = pair.split("=", 1)
    images[name] = reference

print(json.dumps({"registry": registry, "tag": tag, "images": images}, indent=2))
PY
)"

printf '%s\n' "$json_payload" > "$OUTPUT"
printf '%s\n' "$json_payload"

printf 'Wrote %s\n' "$OUTPUT" >&2
