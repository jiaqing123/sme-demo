#!/bin/sh
# Runs from the nginx image entrypoint before nginx starts (numbered 30 so it
# runs before 40-runtime-config.sh). Renders the server block from
# /etc/nginx/templates/default.conf.template into /tmp/conf.d/default.conf,
# which is where web/nginx/nginx.conf expects it.
#
# The proxy upstreams and DNS resolver are environment-specific: Docker Compose
# uses service names on Docker's embedded DNS (127.0.0.11), while Azure
# Container Apps uses environment FQDNs and the platform resolver. Only the
# @...@ placeholders are replaced. nginx runtime variables such as $host are
# left for nginx itself, which is why this script uses sed rather than envsubst.
set -eu

template="/etc/nginx/templates/default.conf.template"
output_dir="/tmp/conf.d"
output="$output_dir/default.conf"

upstream_auth="${UPSTREAM_AUTH:-http://auth:8081}"
upstream_api="${UPSTREAM_API:-http://api:8082}"
nginx_resolver="${NGINX_RESOLVER:-127.0.0.11}"

fail() {
    printf '30-upstream-config.sh: ERROR: %s\n' "$*" >&2
    exit 1
}

# An upstream must be an absolute http(s) URL with a non-empty host, and must
# not contain characters that would corrupt the rendered nginx config or inject
# a directive. Rejecting '$' also prevents nginx variable expansion/injection.
validate_upstream() {
    _var="$1"
    _url="$2"

    case "$_url" in
        *[[:space:]]*)
            fail "$_var must not contain whitespace (got: '$_url')" ;;
    esac
    case "$_url" in
        *';'*|*'$'*|*'"'*|*"'"*|*'#'*|*'{'*|*'}'*)
            fail "$_var must not contain ';', '\$', '#', '{', '}' or quotes (got: '$_url')" ;;
    esac
    case "$_url" in
        http://*|https://*) ;;
        *)
            fail "$_var must be an absolute http:// or https:// URL with a host (got: '$_url')" ;;
    esac

    _rest="${_url#*://}"
    _authority="${_rest%%[/?#]*}"
    [ -n "$_authority" ] || fail "$_var has no host (got: '$_url')"

    # Strip an optional :port, keeping a bracketed IPv6 literal intact.
    case "$_authority" in
        \[*\]:*) _host="${_authority%:*}" ;;
        \[*\])   _host="$_authority" ;;
        *:*)     _host="${_authority%:*}" ;;
        *)       _host="$_authority" ;;
    esac
    [ -n "$_host" ] || fail "$_var has no host (got: '$_url')"
}

# The resolver must be a bare IPv4 address: four decimal octets, 0-255 each.
validate_resolver() {
    _resolver="$1"

    case "$_resolver" in
        *[!0-9.]*)
            fail "NGINX_RESOLVER must be a bare IPv4 address (got: '$_resolver')" ;;
    esac

    _saved_ifs="$IFS"
    IFS=.
    set -- $_resolver
    IFS="$_saved_ifs"

    [ "$#" -eq 4 ] || fail "NGINX_RESOLVER must be a bare IPv4 address (got: '$_resolver')"
    for _octet in "$@"; do
        case "$_octet" in
            ""|*[!0-9]*)
                fail "NGINX_RESOLVER must be a bare IPv4 address (got: '$_resolver')" ;;
        esac
        [ "$_octet" -le 255 ] || fail "NGINX_RESOLVER must be a bare IPv4 address (got: '$_resolver')"
    done
}

# Escape a literal value for the replacement side of s|...|...|: backslash,
# ampersand and the '|' delimiter are the only characters that need it.
escape_for_sed() {
    printf '%s' "$1" | sed -e 's/[\\&|]/\\&/g'
}

validate_upstream "UPSTREAM_AUTH" "$upstream_auth"
validate_upstream "UPSTREAM_API" "$upstream_api"
validate_resolver "$nginx_resolver"

[ -r "$template" ] || fail "template is missing or unreadable: $template"

mkdir -p "$output_dir"

sed \
    -e "s|@UPSTREAM_AUTH@|$(escape_for_sed "$upstream_auth")|g" \
    -e "s|@UPSTREAM_API@|$(escape_for_sed "$upstream_api")|g" \
    -e "s|@NGINX_RESOLVER@|$(escape_for_sed "$nginx_resolver")|g" \
    "$template" > "$output"

chmod 0644 "$output"
