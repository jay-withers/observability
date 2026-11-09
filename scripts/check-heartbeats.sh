#!/usr/bin/env bash
# Works out whether every Container Apps job on the shared environment is
# still running on schedule, and prints one OpsEvents_CL `heartbeat` row per
# job as a JSON array, for schedule-heartbeats.yml to post:
#
#   check-heartbeats.sh <workspace-customer-id> <subscription-id> [run-url]
#
# No job list to maintain: each job's cadence is learned from 30 days of
# ContainerAppSystemLogs_CL. A job is `overdue` when its last successful run
# (Reason `Completed`) is older than 1.5x its longest gap between starts
# (Reason `SuccessfulCreate`), with a 2-hour floor for jobs that run every few
# minutes. Checked against real data on 2026-10-31: all 16 live jobs `ok`,
# from market-agent's 5-minute syncs to security-testing's irregular scans.
#
# Trade-offs, stated:
#   - A job is covered once it has completed twice. One renamed or never
#     successful, like the old `caj-*-dev` names from early October, is
#     ignored rather than reported overdue for a month.
#   - An irregular job is caught only after its longest normal gap, 1.5x.
#   - A deliberately deleted job reads `removed`, not `overdue`, until it
#     ages out of the 30 days: the jobs that still exist are listed from
#     Resource Graph first. It still gets a row, rather than none, so its
#     latest row stops being the last `overdue` one and the alert clears (the
#     same reason the session check posts `ok` rows). An empty list fails the
#     run instead of marking every job `removed`.
#
# Also one `heartbeat` row for Deployments_CL itself, `overdue` after 14 days
# with no deploy at all, so a broken deploy event can't pass for a quiet week.
#
# And one `heartbeat` row per daily check that writes OpsEvents_CL itself
# (every root's drift, plus cost-feed, expiry and session here), `overdue`
# when its newest row is over 36 hours old. A run that dies before it can post
# (a failed login, a broken checkout) otherwise leaves nothing behind but a red
# run: ops_events_silent can't see it while these daily rows keep coming.
# Drift is tracked per root, the others per Kind. A root whose drift check is
# removed reads `overdue` until it ages out of the 30 days, as a deleted job
# does.
#
# The workspace is read with `az rest` against the Log Analytics query API,
# and the live jobs from Resource Graph: the caller's identity needs Reader
# on the subscription (read on the workspace alone isn't enough).
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <workspace-customer-id> <subscription-id> [run-url]" >&2
  exit 2
fi
workspace="$1" subscription="$2" run_url="${3:-}"

graph="$(jq -n --arg s "$subscription" '{subscriptions: [$s],
  query: "resources | where type =~ \"microsoft.app/jobs\" | project name",
  options: {resultFormat: "objectArray", "$top": 1000}}')"
live="$(az rest --method post \
  --url "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01" \
  --body "$graph" | jq -c '[.data[].name]')"
if [[ "$live" == "[]" ]]; then
  echo "Resource Graph lists no Container Apps jobs in $subscription; refusing to mark every job removed" >&2
  exit 1
fi

read -r -d '' query <<'KQL' || true
let starts = ContainerAppSystemLogs_CL
  | where isnotempty(JobName_s) and Reason_s == "SuccessfulCreate"
  | summarize start = min(TimeGenerated) by JobName_s, ExecutionName_s;
let gaps = starts
  | order by JobName_s asc, start asc
  | extend gap = iff(prev(JobName_s) == JobName_s, start - prev(start), timespan(null))
  | summarize maxgap = max(gap) by JobName_s;
let jobs = ContainerAppSystemLogs_CL
  | where isnotempty(JobName_s) and Reason_s == "Completed"
  | summarize completions = dcount(ExecutionName_s), lastok = max(TimeGenerated) by JobName_s
  | where completions >= 2
  | join kind=inner gaps on JobName_s
  | extend allowed = max_of(coalesce(maxgap, 0s) * 1.5, 2h)
  | extend removed = JobName_s !in~ (live)
  | project Name = JobName_s,
      Status = case(removed, "removed", now() - lastok > allowed, "overdue", "ok"),
      Detail = iff(removed,
        strcat("No longer exists. Last success ", format_datetime(lastok, "yyyy-MM-dd HH:mm"),
          " UTC; listed until it ages out of 30 days of logs."),
        strcat("Last success ", format_datetime(lastok, "yyyy-MM-dd HH:mm"),
          " UTC. Overdue after ", round(allowed / 1h, 1), "h (1.5x its longest gap in 30 days, at least 2h)."));
let deploys = Deployments_CL
  | summarize lastdeploy = max(TimeGenerated)
  | project Name = "Deployments_CL",
      Status = iff(isnull(lastdeploy) or now() - lastdeploy > 14d, "overdue", "ok"),
      Detail = iff(isnull(lastdeploy), "No deploy rows in 30 days.",
        strcat("Last deploy row ", format_datetime(lastdeploy, "yyyy-MM-dd HH:mm"), " UTC. Overdue after 14 days."));
let checks = OpsEvents_CL
  | where Kind in ("drift", "cost-feed", "expiry", "session")
  | extend Check = iff(Kind == "drift", strcat("drift ", Name), Kind)
  | summarize lastrow = max(TimeGenerated) by Check
  | project Name = strcat("check: ", Check),
      Status = iff(now() - lastrow > 36h, "overdue", "ok"),
      Detail = strcat("Last row ", format_datetime(lastrow, "yyyy-MM-dd HH:mm"),
        " UTC. Overdue after 36h (a daily check).");
union jobs, deploys, checks
| order by Name asc
KQL

# The live names go in as a KQL dynamic literal: JSON is valid there.
body="$(jq -n --arg q "let live = dynamic($live);"$'\n'"$query" '{query: $q, timespan: "P30D"}')"
result="$(az rest --method post \
  --url "https://api.loganalytics.io/v1/workspaces/$workspace/query" \
  --resource https://api.loganalytics.io --body "$body")"

time="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
jq -c --arg time "$time" --arg run "$run_url" '
  .tables[0] as $t
  | [$t.rows[] | [$t.columns[].name] as $c | [$c, .] | transpose | map({(.[0]): .[1]}) | add
     | {TimeGenerated: $time, Repo: "observability", Environment: "dev", Kind: "heartbeat",
        Name, Status, Detail, RunUrl: $run}]' <<<"$result" \
  | tee >(jq -r '.[] | "\(.Name): \(.Status) - \(.Detail)"' >&2)
