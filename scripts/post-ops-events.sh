#!/usr/bin/env bash
# Posts a JSON array of rows through the Logs Ingestion API, for the
# schedule-* workflows. The stream defaults to OpsEvents_CL; schedule-costs
# names Costs_CL:
#
#   post-ops-events.sh <rows.json> [table]
#
# Needs an `az` login with Monitoring Metrics Publisher on the platform's
# events rule, and EVENTS_INGESTION_ENDPOINT and EVENTS_DCR_IMMUTABLE_ID in the
# environment (set on `dev` by github-repos for `send_events`). Fails loudly:
# a silent gap here would look exactly like a healthy night.
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 <rows.json> [table]" >&2
  exit 2
fi
rows="$1" table="${2:-OpsEvents_CL}"

if [[ -z "${EVENTS_INGESTION_ENDPOINT:-}" || -z "${EVENTS_DCR_IMMUTABLE_ID:-}" ]]; then
  echo "post-ops-events: EVENTS_INGESTION_ENDPOINT or EVENTS_DCR_IMMUTABLE_ID is not set." >&2
  exit 1
fi

token="$(az account get-access-token --resource https://monitor.azure.com --query accessToken -o tsv)"
out="$(mktemp)"
code="$(curl -sS -o "$out" -w '%{http_code}' -X POST \
  "${EVENTS_INGESTION_ENDPOINT%/}/dataCollectionRules/${EVENTS_DCR_IMMUTABLE_ID}/streams/Custom-${table}?api-version=2023-01-01" \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
  --data @"$rows")"
# 204 is the only success: the API accepts the batch whole or not at all.
if [[ "$code" != "204" ]]; then
  echo "post-ops-events: HTTP $code: $(cat "$out")" >&2
  exit 1
fi
echo "post-ops-events: recorded $(jq length "$rows") $table row(s)." >&2
