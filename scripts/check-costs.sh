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
# One query through `az rest`, following nextLink: Reader on the subscription
# is enough to read costs. Cost Management throttles hard (a 429 on a second
# run ten minutes after the first), so each page retries a 429 with a growing
# wait, up to about five minutes in all. Any other error fails at once.
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
pages=()
err="$(mktemp)"
while [[ -n "$url" ]]; do
  for wait in 15 30 60 90 120 0; do
    if page="$(az rest --method post --url "$url" --body "$body" 2>"$err")"; then
      break
    fi
    if ! grep -q 'Too Many Requests' "$err" || (( wait == 0 )); then
      cat "$err" >&2
      exit 1
    fi
    echo "check-costs: throttled, retrying in ${wait}s." >&2
    sleep "$wait"
  done
  pages+=("$page")
  url="$(jq -r '.properties.nextLink // empty' <<<"$page")"
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
