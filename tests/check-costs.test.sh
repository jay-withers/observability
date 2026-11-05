# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-costs.sh against a stubbed `az rest` and `sleep`.

costs=("$ROOT/scripts/check-costs.sh" sub-123 https://run.example/1)

# costs_page <rows-json> [next-link]: one Cost Management query response, with
# the columns in the order the API returns them.
costs_page() {
  jq -cn --argjson rows "$1" --arg next "${2:-}" '{properties: {
    nextLink: (if $next == "" then null else $next end),
    columns: [{name: "Cost"}, {name: "UsageDate"}, {name: "ResourceGroupName"},
              {name: "ServiceName"}, {name: "Currency"}],
    rows: $rows}}'
}

stub_sleep() {
  stub sleep <<SH
echo "\$1" >>"$tmp/slept"
SH
}

test_costs_usage() {
  run "$ROOT/scripts/check-costs.sh"
  assert_eq "$status" 2 status
}

test_costs_maps_rows_and_drops_zero() {
  costs_page '[
    [1.5, 20261101, "RG-Finances-Dev", "Container Apps", "GBP"],
    [0, 20261101, "rg-gymlog-dev", "Storage", "GBP"],
    [0.25, 20261102, "", "Bandwidth", "GBP"]]' >"$tmp/page.json"
  stub az <<SH
cat "$tmp/page.json"
SH

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(field length)" 2 "row count (the zero row dropped)"
  assert_eq "$(field '.[0].ResourceGroup')" rg-finances-dev "resource group, lower-cased"
  assert_eq "$(field '.[0].UsageDate')" 2026-11-01T00:00:00Z "UsageDate"
  assert_eq "$(field '.[0].ServiceName')" "Container Apps" ServiceName
  assert_eq "$(field '.[0].Cost')" 1.5 Cost
  assert_eq "$(field '.[0].Currency')" GBP Currency
  assert_eq "$(field '.[0].RunUrl')" https://run.example/1 RunUrl
  assert_eq "$(field '.[1].ResourceGroup')" "(none)" "subscription-level charge"
}

test_costs_follows_next_link() {
  costs_page '[[1, 20261101, "rg-a", "S", "GBP"]]' "https://management.azure.com/next?page=2" >"$tmp/page1.json"
  costs_page '[[2, 20261102, "rg-b", "S", "GBP"]]' >"$tmp/page2.json"
  stub az <<SH
echo call >>"$tmp/calls"
case "\$*" in
  *page=2*) cat "$tmp/page2.json" ;;
  *) cat "$tmp/page1.json" ;;
esac
SH

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(wc -l <"$tmp/calls")" 2 "az calls"
  assert_eq "$(field '[.[].ResourceGroup] | join(",")')" rg-a,rg-b "rows from both pages"
}

test_costs_retries_when_throttled() {
  costs_page '[[1, 20261101, "rg-a", "S", "GBP"]]' >"$tmp/page.json"
  stub az <<SH
if [[ ! -f "$tmp/throttled" ]]; then
  touch "$tmp/throttled"
  echo "ERROR: Too Many Requests" >&2
  exit 1
fi
cat "$tmp/page.json"
SH
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(cat "$tmp/slept")" 15 "waits before the retry"
  assert_eq "$(field length)" 1 "row count"
}

test_costs_gives_up_after_five_waits() {
  stub az <<'SH'
echo "ERROR: Too Many Requests" >&2
exit 1
SH
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 1 status
  assert_eq "$(paste -sd' ' "$tmp/slept")" "15 30 60 90 120" waits
  assert_eq "$out" "" "stdout (nothing to post)"
}

test_costs_fails_at_once_on_other_errors() {
  stub az <<'SH'
echo "ERROR: Forbidden" >&2
exit 1
SH
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 1 status
  assert_contains "$err" Forbidden stderr
  [[ ! -f "$tmp/slept" ]] || fail "slept on a non-throttling error"
}
