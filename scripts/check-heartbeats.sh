#!/usr/bin/env bash
# Works out whether every Container Apps job on the shared environment is
# still running on schedule, and prints one OpsEvents_CL `heartbeat` row per
# job as a JSON array, for schedule-heartbeats.yml to post:
#
#   check-heartbeats.sh <workspace-customer-id> [run-url]
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
#   - A deliberately deleted job reads `overdue` until it ages out of the
#     30 days. Its alert is one email (auto-mitigated rules don't repeat).
#
# Also one `heartbeat` row for Deployments_CL itself, `overdue` after 14 days
# with no deploy at all, so a broken deploy event can't pass for a quiet week.
#
# The workspace is read with `az rest` against the Log Analytics query API;
# the caller's identity needs read on the workspace (Reader is enough).
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <workspace-customer-id> [run-url]" >&2
  exit 2
fi
workspace="$1" run_url="${2:-}"

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
  | project Name = JobName_s,
      Status = iff(now() - lastok > allowed, "overdue", "ok"),
      Detail = strcat("Last success ", format_datetime(lastok, "yyyy-MM-dd HH:mm"),
        " UTC. Overdue after ", round(allowed / 1h, 1), "h (1.5x its longest gap in 30 days, at least 2h).");
let deploys = Deployments_CL
  | summarize lastdeploy = max(TimeGenerated)
  | project Name = "Deployments_CL",
      Status = iff(isnull(lastdeploy) or now() - lastdeploy > 14d, "overdue", "ok"),
      Detail = iff(isnull(lastdeploy), "No deploy rows in 30 days.",
        strcat("Last deploy row ", format_datetime(lastdeploy, "yyyy-MM-dd HH:mm"), " UTC. Overdue after 14 days."));
union jobs, deploys
| order by Name asc
KQL

body="$(jq -n --arg q "$query" '{query: $q, timespan: "P30D"}')"
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
