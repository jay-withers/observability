#!/usr/bin/env bash
# Reads the last seven days of cost per resource group and service from Cost
# Management and prints them as Costs_CL rows in a JSON array, for
# schedule-costs.yml to post:
#
#   check-costs.sh <subscription-id> [run-url]
#
# Seven days, every night, because Azure revises a day's figure for about
# three days after it; readers take the latest row per UsageDate,
# ResourceGroup and ServiceName. Rows of zero are dropped: most services on a
# scale-to-zero estate cost nothing most days.
#
# ActualCost is the charge before any credit, so on a Visual Studio
# subscription this is what the estate would cost on pay-as-you-go.
# Subscription-level charges with no resource group read `(none)`.
#
# One query through curl, following nextLink: Reader on the subscription is
# enough to read costs. Cost Management throttles hard, and a 429 can last
# longer than five minutes (observability runs on 2026-11-04 and 11-06 were
# refused for all of that), so a 429 waits as long as its retry-after headers
# ask (the longest of the qpu, entity, tenant and plain ones), or 15, 30, 60,
# 90, then 120s when it names none, up to COSTS_MAX_WAIT seconds of waiting in
# all (default 1200). A wait that would pass that fails at once rather than
# sleeping through the job, and so does any other error. curl rather than
# `az rest`, which hides the headers.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <subscription-id> [run-url]" >&2
  exit 2
fi
subscription="$1" run_url="${2:-}"

from="$(date -u -d '7 days ago' +%Y-%m-%d)"
to="$(date -u +%Y-%m-%d)"
body="$(jq -n --arg from "$from" --arg to "$to" '{
  type: "ActualCost",
  timeframe: "Custom",
  timePeriod: {from: $from, to: $to},
  dataset: {
    granularity: "Daily",
    aggregation: {totalCost: {name: "Cost", function: "Sum"}},
    grouping: [
      {type: "Dimension", name: "ResourceGroupName"},
      {type: "Dimension", name: "ServiceName"}
    ]
  }
}')"

url="https://management.azure.com/subscriptions/$subscription/providers/Microsoft.CostManagement/query?api-version=2023-11-01"
token="$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)"
max_wait="${COSTS_MAX_WAIT:-1200}"
fallback=(15 30 60 90 120)
waited=0 throttles=0
headers="$(mktemp)" response="$(mktemp)"
pages=()
while [[ -n "$url" ]]; do
  while :; do
    code="$(curl -sS -D "$headers" -o "$response" -w '%{http_code}' -X POST "$url" \
      -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
      --data "$body")"
    [[ "$code" == 200 ]] && break
    if [[ "$code" != 429 ]]; then
      echo "check-costs: HTTP $code: $(cat "$response")" >&2
      exit 1
    fi
    # Every retry-after header in seconds; an HTTP-date one is ignored.
    delay="$(tr -d '\r' <"$headers" \
      | sed -nE 's/^(x-ms-ratelimit-microsoft\.costmanagement-[a-z]+-)?retry-after: *([0-9]+) *$/\2/Ip' \
      | sort -n | tail -1)"
    if [[ -z "$delay" ]]; then
      i=$((throttles < ${#fallback[@]} ? throttles : ${#fallback[@]} - 1))
      delay="${fallback[$i]}"
    fi
    if ((waited + delay > max_wait)); then
      echo "check-costs: still throttled after ${waited}s of waiting; a further ${delay}s would pass ${max_wait}s. $(cat "$response")" >&2
      exit 1
    fi
    echo "check-costs: throttled, retrying in ${delay}s." >&2
    sleep "$delay"
    waited=$((waited + delay)) throttles=$((throttles + 1))
  done
  pages+=("$(cat "$response")")
  url="$(jq -r '.properties.nextLink // empty' "$response")"
done

time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf '%s\n' "${pages[@]}" | jq -cs --arg time "$time" --arg run "$run_url" '
  [.[] | .properties as $p
   | $p.rows[] | [[$p.columns[].name], .] | transpose | map({(.[0]): .[1]}) | add
   | select(.Cost != 0)
   | (.UsageDate | tostring) as $d
   | {TimeGenerated: $time,
      UsageDate: "\($d[0:4])-\($d[4:6])-\($d[6:8])T00:00:00Z",
      ResourceGroup: (if (.ResourceGroupName // "") == "" then "(none)" else (.ResourceGroupName | ascii_downcase) end),
      ServiceName: (.ServiceName // "(none)"),
      Cost, Currency, RunUrl: $run}]' \
  | tee >(jq -r 'group_by(.Currency)[] | "\(length) row(s), \(map(.Cost) | add * 100 | round / 100) \(.[0].Currency) over seven days"' >&2)
