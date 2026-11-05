# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-heartbeats.sh against a stubbed `az rest`. The KQL is not run
# here; what is tested is the request and the mapping of its result to rows.

# stub_az <rows-json>: answers with one Log Analytics table, and keeps the
# request's URL and body.
stub_az() {
  jq -cn --argjson rows "$1" '{tables: [{name: "PrimaryResult",
    columns: [{name: "Name"}, {name: "Status"}, {name: "Detail"}], rows: $rows}]}' >"$tmp/result.json"
  stub az <<SH
while ((\$#)); do
  case "\$1" in
    --url) echo "\$2" >"$tmp/url" ;;
    --body) printf '%s' "\$2" >"$tmp/body" ;;
  esac
  shift
done
cat "$tmp/result.json"
SH
}

test_heartbeats_usage() {
  run "$ROOT/scripts/check-heartbeats.sh"
  assert_eq "$status" 2 status
}

test_heartbeats_request() {
  stub_az '[]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123
  assert_eq "$status" 0 status
  assert_eq "$(cat "$tmp/url")" https://api.loganalytics.io/v1/workspaces/ws-123/query URL
  assert_eq "$(jq -r .timespan "$tmp/body")" P30D timespan
  assert_contains "$(jq -r .query "$tmp/body")" "union jobs, deploys, checks" query
}

test_heartbeats_maps_rows() {
  stub_az '[
    ["caj-gymlog-sync-dev", "ok", "Last success 2026-11-05 09:00 UTC."],
    ["Deployments_CL", "overdue", "No deploy rows in 30 days."]]'

  run "$ROOT/scripts/check-heartbeats.sh" ws-123 https://run.example/1
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

  run "$ROOT/scripts/check-heartbeats.sh" ws-123
  assert_eq "$out" "[]" stdout
}
