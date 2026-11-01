#!/usr/bin/env bash
# Looks for billable resources that are meant to exist only for a working
# session (a firewall, an AKS cluster, a VM) and prints one OpsEvents_CL
# `session` row per category as a JSON array, for
# schedule-session-resources.yml to post:
#
#   check-session-resources.sh <subscription-id> [run-url]
#
# Run nightly, so anything still up is a session that was never ended. Status
# is `running` when a category has one, otherwise `ok`. Every category gets a
# row on every run, so a deleted firewall clears the platform's ops_event_bad
# alert the next night with no bookkeeping here.
#
# What counts, and why not just "anything hourly":
#   - firewall, vpn-gateway, app-gateway, nat-gateway: billed while they
#     exist. bastion likewise, except the free Developer SKU.
#   - aks: billed while its power state is Running; a stopped cluster is not.
#   - vm: billed unless deallocated. "Stopped" from inside the guest still
#     pays for the compute.
#   - workload-profile: a Container Apps environment with any profile other
#     than Consumption, which bills per hour whether or not anything runs.
#   The shared Postgres server is a standing cost by decision, not a session,
#   so it is not here.
#
# A session meant to outlive the night: tag the resource
# `session-until=YYYY-MM-DD` and it reads `ok` up to and including that day,
# with the date in Detail.
#
# One Resource Graph query through `az rest`, so the caller needs Reader on
# the subscription and no CLI extension.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <subscription-id> [run-url]" >&2
  exit 2
fi
subscription="$1" run_url="${2:-}"

read -r -d '' query <<'KQL' || true
resources
| extend Category = case(
    type =~ "microsoft.network/azurefirewalls", "firewall",
    type =~ "microsoft.containerservice/managedclusters", "aks",
    type =~ "microsoft.compute/virtualmachines", "vm",
    type =~ "microsoft.network/bastionhosts", "bastion",
    type =~ "microsoft.network/virtualnetworkgateways", "vpn-gateway",
    type =~ "microsoft.network/applicationgateways", "app-gateway",
    type =~ "microsoft.network/natgateways", "nat-gateway",
    type =~ "microsoft.app/managedenvironments", "workload-profile",
    "")
| where Category != ""
| project Category, name, resourceGroup,
    Until = tostring(tags["session-until"]),
    AksPower = tostring(properties.powerState.code),
    VmPower = tostring(properties.extended.instanceView.powerState.code),
    Sku = tostring(sku.name),
    Profiles = properties.workloadProfiles
KQL

body="$(jq -n --arg s "$subscription" --arg q "$query" \
  '{subscriptions: [$s], query: $q, options: {resultFormat: "objectArray"}}')"
result="$(az rest --method post \
  --url "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01" \
  --body "$body")"

time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
today="$(date -u +%Y-%m-%d)"
jq -c --arg time "$time" --arg today "$today" --arg run "$run_url" '
  def billing:
    if .Category == "aks" then .AksPower == "Running"
    elif .Category == "vm" then (.VmPower | ascii_downcase) != "powerstate/deallocated"
    elif .Category == "bastion" then .Sku != "Developer"
    elif .Category == "workload-profile" then
      [(.Profiles // [])[] | select(.workloadProfileType != "Consumption")] | length > 0
    else true end;
  def label: "\(.resourceGroup)/\(.name)";

  [.data[] | select(billing)] as $up
  | ["firewall", "aks", "vm", "bastion", "vpn-gateway", "app-gateway", "nat-gateway", "workload-profile"]
  | map(. as $c
      | [$up[] | select(.Category == $c)] as $hits
      | [$hits[] | select(.Until == "" or .Until < $today)] as $left
      | [$hits[] | select(.Until != "" and .Until >= $today)] as $kept
      | {TimeGenerated: $time, Repo: "observability", Environment: "dev", Kind: "session", Name: $c,
         Status: (if ($left | length) > 0 then "running" else "ok" end),
         Detail: (
           [ (if ($left | length) > 0 then "Still running: \([$left[] | label] | join(", ")). Tag session-until=YYYY-MM-DD to keep it." else empty end),
             (if ($kept | length) > 0 then "Kept on purpose: \([$kept[] | "\(label) until \(.Until)"] | join(", "))." else empty end) ]
           | if length == 0 then "Nothing running." else join(" ") end),
         RunUrl: $run})' <<<"$result" \
  | tee >(jq -r '.[] | "\(.Name): \(.Status) - \(.Detail)"' >&2)
