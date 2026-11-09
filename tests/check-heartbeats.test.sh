# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-heartbeats.sh against a stubbed `az rest`. The KQL is not run
# here; what is tested is the request and the mapping of its result to rows.

# stub_az <rows-json> [live-jobs-json]: answers Resource Graph with the live
# job names (one by default) and Log Analytics with one table, and keeps each
# request's body.
stub_az() {
  jq -cn --argjson rows "$1" '{tables: [{name: "PrimaryResult",
    columns: [{name: "Name"}, {name: "Status"}, {name: "Detail"}], rows: $rows}]}' >"$tmp/result.json"
  jq -cn --argjson names "${2:-[\"caj-gymlog-sync-dev\"]}" '{data: [$names[] | {name: .}]}' >"$tmp/graph.json"
  stub az <<SH
while ((\$#)); do
  case "\$1" in
    --url) url="\$2" ;;
    --body) body="\$2" ;;
  esac
  shift
done
case "\$url" in
  *ResourceGraph*) printf '%s' "\$body" >"$tmp/graph-body"; cat "$tmp/graph.json" ;;
  *) echo "\$url" >"$tmp/url"; printf '%s' "\$body" >"$tmp/body"; cat "$tmp/result.json" ;;
esac
SH
}

test_heartbeats_usage() {
  run "$ROOT/scripts/check-heartbeats.sh" ws-123
  assert_eq "$status" 2 status
}

test_heartbeats_request() {
  stub_az '[]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 sub-1
  assert_eq "$status" 0 status
  assert_eq "$(cat "$tmp/url")" https://api.loganalytics.io/v1/workspaces/ws-123/query URL
  assert_eq "$(jq -r .timespan "$tmp/body")" P30D timespan
  assert_contains "$(jq -r .query "$tmp/body")" "union jobs, deploys, checks" query
  assert_eq "$(jq -c .subscriptions "$tmp/graph-body")" '["sub-1"]' "graph subscriptions"
  assert_contains "$(jq -r .query "$tmp/graph-body")" 'microsoft.app/jobs' "graph query"
}

test_heartbeats_passes_live_jobs_to_kql() {
  stub_az '[]' '["caj-gymlog-sync-dev", "caj-marketagent-dev-agent-sp500"]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 sub-1
  assert_eq "$status" 0 status
  assert_eq "$(jq -r .query "$tmp/body" | head -1)" \
    'let live = dynamic(["caj-gymlog-sync-dev","caj-marketagent-dev-agent-sp500"]);' "first KQL line"
  assert_contains "$(jq -r .query "$tmp/body")" '!in~ (live)' "jobs filtered on live"
}

test_heartbeats_no_live_jobs_fails() {
  stub_az '[]' '[]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 sub-1
  assert_eq "$status" 1 status
  assert_eq "$out" "" stdout
  assert_contains "$err" "lists no Container Apps jobs" stderr
}

test_heartbeats_maps_rows() {
  stub_az '[
    ["caj-gymlog-sync-dev", "ok", "Last success 2026-11-05 09:00 UTC."],
    ["Deployments_CL", "overdue", "No deploy rows in 30 days."]]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 sub-1 https://run.example/1
  assert_eq "$status" 0 status
  assert_eq "$(field length)" 2 "row count"
  assert_eq "$(field '.[0] | "\(.Name)|\(.Status)|\(.Detail)"')" \
    "caj-gymlog-sync-dev|ok|Last success 2026-11-05 09:00 UTC." "first row"
  assert_eq "$(field '.[1].Status')" overdue "second row's Status"
  assert_eq "$(field '[.[] | .Kind, .Repo, .Environment, .RunUrl] | unique | join(",")')" \
    "dev,heartbeat,https://run.example/1,observability" "Kind, Repo, Environment and RunUrl"
}

test_heartbeats_no_rows() {
  stub_az '[]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 sub-1
  assert_eq "$out" "[]" stdout
}
