# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/post-ops-events.sh against a stubbed `az` (the token) and `curl`.

# stub_curl <http-code> [response-body]: answers with that code, and keeps
# its arguments one per line in $tmp/curl-args.
stub_curl() {
  stub az <<'SH'
echo token-abc
SH
  printf '%s' "${2:-}" >"$tmp/curl-body"
  stub curl <<SH
printf '%s\n' "\$@" >"$tmp/curl-args"
while ((\$#)); do
  if [[ "\$1" == -o ]]; then cat "$tmp/curl-body" >"\$2"; fi
  shift
done
printf '%s' "$1"
SH
  echo '[{"Name": "a"}, {"Name": "b"}]' >"$tmp/rows.json"
  export EVENTS_INGESTION_ENDPOINT=https://dce.example.ingest.monitor.azure.com/
  export EVENTS_DCR_IMMUTABLE_ID=dcr-123
}

test_post_usage() {
  run "$ROOT/scripts/post-ops-events.sh"
  assert_eq "$status" 2 "status with no arguments"
  run "$ROOT/scripts/post-ops-events.sh" a b c
  assert_eq "$status" 2 "status with three arguments"
}

test_post_needs_the_ingestion_settings() {
  stub_curl 204
  unset EVENTS_DCR_IMMUTABLE_ID

  run "$ROOT/scripts/post-ops-events.sh" "$tmp/rows.json"
  assert_eq "$status" 1 status
  assert_contains "$err" "is not set" stderr
  [[ ! -f "$tmp/curl-args" ]] || fail "posted without the settings"
}

test_post_defaults_to_ops_events() {
  stub_curl 204

  run "$ROOT/scripts/post-ops-events.sh" "$tmp/rows.json"
  assert_eq "$status" 0 status
  grep -qxF \
    "https://dce.example.ingest.monitor.azure.com/dataCollectionRules/dcr-123/streams/Custom-OpsEvents_CL?api-version=2023-01-01" \
    "$tmp/curl-args" || fail "URL (trailing slash trimmed, OpsEvents_CL stream): $(cat "$tmp/curl-args")"
  grep -qxF "Authorization: Bearer token-abc" "$tmp/curl-args" || fail "no bearer token header"
  grep -qxF "@$tmp/rows.json" "$tmp/curl-args" || fail "rows not sent as the body"
  assert_contains "$err" "recorded 2 OpsEvents_CL row(s)" stderr
}

test_post_names_the_table() {
  stub_curl 204

  run "$ROOT/scripts/post-ops-events.sh" "$tmp/rows.json" Costs_CL
  assert_eq "$status" 0 status
  grep -q '/streams/Custom-Costs_CL?' "$tmp/curl-args" || fail "not posted to the Costs_CL stream"
  assert_contains "$err" "recorded 2 Costs_CL row(s)" stderr
}

test_post_fails_on_an_error_response() {
  stub_curl 400 '{"error": "bad row"}'

  run "$ROOT/scripts/post-ops-events.sh" "$tmp/rows.json"
  assert_eq "$status" 1 status
  assert_contains "$err" 'HTTP 400: {"error": "bad row"}' stderr
}

test_post_treats_200_as_failure() {
  stub_curl 200

  run "$ROOT/scripts/post-ops-events.sh" "$tmp/rows.json"
  assert_eq "$status" 1 "status (204 is the only success)"
}
