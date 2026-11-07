#!/bin/sh
# Runs from the nginx image entrypoint before nginx starts. Writes the runtime
# configuration the SPA reads as window.__SME_CONFIG__. Credentials are only
# emitted when DEMO_MODE is exactly "true"; otherwise the file carries no demo
# hints at all.
set -eu

config_path="/usr/share/nginx/html/config.js"

# JSON-escape a value: backslash first, then double quote. Newlines (never part
# of a demo credential) are removed rather than emitted into invalid JS.
json_escape() {
    printf '%s' "$1" | tr -d '\r\n' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

if [ "${DEMO_MODE:-false}" = "true" ]; then
    cat > "$config_path" <<EOF
window.__SME_CONFIG__ = {
  demoMode: true,
  demoUsername: "$(json_escape "${DEMO_USERNAME:-}")",
  demoPassword: "$(json_escape "${DEMO_PASSWORD:-}")",
  demoOtp: "$(json_escape "${DEMO_OTP:-}")"
};
EOF
else
    cat > "$config_path" <<EOF
window.__SME_CONFIG__ = {
  demoMode: false
};
EOF
fi

chmod 0644 "$config_path"
