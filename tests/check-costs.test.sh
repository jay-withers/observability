# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-costs.sh against a stubbed `az` (the token), `curl` and `sleep`.

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

# stub_responses <response>...: `az` hands out a token and each curl call
# answers with the next response, the last repeating. A response is
# "<code> <body-file> [header-line]...". Each call's URL is kept, one per
# line, in $tmp/urls.
stub_responses() {
  stub az <<'SH'
echo token-abc
SH
  printf '%s\n' "$@" >"$tmp/responses"
  stub curl <<SH
n=\$(( \$(cat "$tmp/count" 2>/dev/null || echo 0) + 1 ))
echo "\$n" >"$tmp/count"
total=\$(wc -l <"$tmp/responses")
read -r -a r < <(sed -n "\$(( n < total ? n : total ))p" "$tmp/responses")
while ((\$#)); do
  case "\$1" in
    -D) : >"\$2"; for h in "\${r[@]:2}"; do printf '%s\r\n' "\${h/=/: }" >>"\$2"; done; shift ;;
    -o) cat "\${r[1]}" >"\$2"; shift ;;
    https://*) echo "\$1" >>"$tmp/urls" ;;
  esac
  shift
done
printf '%s' "\${r[0]}"
SH
  echo '{"error":{"code":"429","message":"Too many requests. Please retry."}}' >"$tmp/429.json"
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
  stub_responses "200 $tmp/page.json"

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
  stub_responses "200 $tmp/page1.json" "200 $tmp/page2.json"

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(sed -n 2p "$tmp/urls")" "https://management.azure.com/next?page=2" "second call follows nextLink"
  assert_eq "$(field '[.[].ResourceGroup] | join(",")')" rg-a,rg-b "rows from both pages"
}

test_costs_waits_as_long_as_the_headers_ask() {
  costs_page '[[1, 20261101, "rg-a", "S", "GBP"]]' >"$tmp/page.json"
  stub_responses \
    "429 $tmp/429.json Retry-After=10 x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after=45 x-ms-ratelimit-microsoft.costmanagement-entity-retry-after=7" \
    "200 $tmp/page.json"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(cat "$tmp/slept")" 45 "the longest retry-after"
  assert_eq "$(field length)" 1 "row count"
}

test_costs_falls_back_without_a_header() {
  costs_page '[[1, 20261101, "rg-a", "S", "GBP"]]' >"$tmp/page.json"
  stub_responses "429 $tmp/429.json" "429 $tmp/429.json" "200 $tmp/page.json"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(paste -sd' ' "$tmp/slept")" "15 30" waits
}

test_costs_keeps_waiting_past_five_minutes() {
  costs_page '[[1, 20261101, "rg-a", "S", "GBP"]]' >"$tmp/page.json"
  stub_responses "429 $tmp/429.json" "429 $tmp/429.json" "429 $tmp/429.json" \
    "429 $tmp/429.json" "429 $tmp/429.json" "429 $tmp/429.json" "200 $tmp/page.json"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(paste -sd' ' "$tmp/slept")" "15 30 60 90 120 120" waits
}

test_costs_gives_up_at_the_wait_budget() {
  stub_responses "429 $tmp/429.json"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 1 status
  assert_eq "$(jq -s add "$tmp/slept")" 1155 "seconds slept (the next 120 would pass 1200)"
  assert_contains "$err" "still throttled after 1155s" stderr
  assert_eq "$out" "" "stdout (nothing to post)"
}

test_costs_will_not_sleep_past_the_budget() {
  stub_responses "429 $tmp/429.json Retry-After=3600"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 1 status
  assert_contains "$err" "a further 3600s" stderr
  [[ ! -f "$tmp/slept" ]] || fail "slept past the budget"
}

test_costs_fails_at_once_on_other_errors() {
  echo '{"error":{"code":"AuthorizationFailed"}}' >"$tmp/403.json"
  stub_responses "403 $tmp/403.json"
  stub_sleep

  run "${costs[@]}"
  assert_eq "$status" 1 status
  assert_contains "$err" "HTTP 403" stderr
  assert_contains "$err" AuthorizationFailed stderr
  [[ ! -f "$tmp/slept" ]] || fail "slept on a non-throttling error"
}
