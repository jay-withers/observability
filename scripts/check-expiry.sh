#!/usr/bin/env bash
# Checks the TLS certificate of every host in a list and prints one
# OpsEvents_CL row per host as a JSON array, for schedule-expiry.yml to post:
#
#   check-expiry.sh <hosts-file> [run-url]
#
# Status is `ok`, `expiring` (fewer than EXPIRY_WARN_DAYS days left, default
# 21) or `failed` (no handshake, or no certificate). Container Apps' managed
# certificates renew themselves; this is what notices when one doesn't.
#
# A TLS handshake only, never an HTTP request: the ingress terminates TLS, so
# a scale-to-zero app is not woken by it.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <hosts-file> [run-url]" >&2
  exit 2
fi
hosts="$1" run_url="${2:-}"
warn_days="${EXPIRY_WARN_DAYS:-21}"
now="$(date -u +%s)"
time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

rows=()
while read -r host; do
  host="${host%%#*}"
  host="${host//[[:space:]]/}"
  [[ -z "$host" ]] && continue

  if end="$(echo | timeout 20 openssl s_client -connect "$host:443" -servername "$host" 2>/dev/null \
    | openssl x509 -noout -enddate 2>/dev/null)"; then
    end="${end#notAfter=}"
    days=$(( ($(date -u -d "$end" +%s) - now) / 86400 ))
    if (( days < warn_days )); then status=expiring; else status=ok; fi
    detail="Certificate expires $(date -u -d "$end" +%Y-%m-%d) ($days days)."
  else
    status=failed
    detail="No TLS certificate from $host:443."
  fi
  echo "$host: $status - $detail" >&2

  rows+=("$(jq -cn --arg time "$time" --arg name "$host" --arg status "$status" \
    --arg detail "$detail" --arg run "$run_url" \
    '{TimeGenerated: $time, Repo: "observability", Environment: "dev", Kind: "expiry",
      Name: $name, Status: $status, Detail: $detail, RunUrl: $run}')")
done < "$hosts"

printf '%s\n' "${rows[@]}" | jq -cs .
