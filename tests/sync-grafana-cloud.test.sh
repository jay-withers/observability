# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/sync-grafana-cloud.sh against a stubbed `curl` playing a stack.

# stub_stack [datasource-code] [folder-code] [stale-uid]: a curl that answers
# like Grafana, logging "METHOD path" per call to $tmp/calls and keeping each
# request body as $tmp/body-<n>.
stub_stack() {
  stub curl <<SH
n=\$(( \$(wc -l <"$tmp/calls" 2>/dev/null || echo 0) + 1 ))
method=GET url= out= headers=
while ((\$#)); do
  case "\$1" in
    -X) method="\$2"; shift ;;
    -o) out="\$2"; shift ;;
    -H) headers="\${2#@}"; shift ;;
    --data) cp "\${2#@}" "$tmp/body-\$n"; shift ;;
    -w|-sS) [[ "\$1" == -w ]] && shift ;;
    *) url="\$1" ;;
  esac
  shift
done
path="\${url#https://stack.example.grafana.net}"
echo "\$method \$path" >>"$tmp/calls"
cp "\$headers" "$tmp/headers-seen"
code=200 body='{}'
case "\$method \$path" in
  "GET /api/datasources/uid/azure-monitor") code=${1:-200} ;;
  "GET /api/folders/platform") code=${2:-200} ;;
  "GET /api/search"*) body='[{"uid": "a"}, {"uid": "${3:-}"}]' ;;
esac
printf '%s' "\$body" >"\$out"
printf '%s' "\$code"
SH
  mkdir "$tmp/dash"
  echo '{"uid": "a", "title": "A", "id": 7, "panels": [{"resources": ["__WORKSPACE_RESOURCE_ID__"]}]}' >"$tmp/dash/a.json"
  export GRAFANA_URL=https://stack.example.grafana.net/ GRAFANA_TOKEN=tok-xyz \
    AZURE_TENANT_ID=tenant-1 AZURE_SUBSCRIPTION_ID=sub-1 \
    GRAFANA_AZURE_CLIENT_ID=client-1 GRAFANA_AZURE_CLIENT_SECRET=shh-secret GITHUB_SHA=abc123
}

test_sync_needs_its_settings() {
  stub_stack
  unset GRAFANA_AZURE_CLIENT_SECRET

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 1 status
  assert_contains "$err" "GRAFANA_AZURE_CLIENT_SECRET is not set" stderr
  [[ ! -f "$tmp/calls" ]] || fail "called the stack without its settings"
}

test_sync_updates_an_existing_stack() {
  stub_stack 200 200

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 0 "status: $err"
  grep -qxF "PUT /api/datasources/uid/azure-monitor" "$tmp/calls" || fail "data source not updated: $(cat "$tmp/calls")"
  ! grep -q "POST /api/folders" "$tmp/calls" || fail "recreated an existing folder"
  grep -qxF "Authorization: Bearer tok-xyz" "$tmp/headers-seen" || fail "token not sent as a header file"
  ! grep -rq "shh-secret\|tok-xyz" "$tmp/calls" || fail "a secret reached curl's arguments"
}

test_sync_creates_on_a_new_stack() {
  stub_stack 404 404

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 0 "status: $err"
  grep -qxF "POST /api/datasources" "$tmp/calls" || fail "data source not created"
  grep -qxF "POST /api/folders" "$tmp/calls" || fail "folder not created"
  local ds
  ds="$(sed -n '\|^POST /api/datasources$|=' "$tmp/calls")"
  assert_eq "$(jq -r '.jsonData.azureAuthType + " " + .jsonData.clientId + " " + .secureJsonData.clientSecret' "$tmp/body-$ds")" \
    "clientsecret client-1 shh-secret" "data source body"
}

test_sync_fills_in_the_workspace() {
  stub_stack

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 0 "status: $err"
  local n
  n="$(sed -n '\|POST /api/dashboards/db|=' "$tmp/calls")"
  assert_eq "$(jq -r '.dashboard.panels[0].resources[0]' "$tmp/body-$n")" \
    "/subscriptions/sub-1/resourceGroups/rg-platform-dev/providers/Microsoft.OperationalInsights/workspaces/log-platform-dev" workspace
  assert_eq "$(jq -c '[.dashboard.id, .folderUid, .overwrite, .message]' "$tmp/body-$n")" \
    '[null,"platform",true,"observability abc123"]' "dashboard wrapper"
}

test_sync_deletes_what_git_dropped() {
  stub_stack 200 200 stale

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 0 "status: $err"
  grep -qxF "DELETE /api/dashboards/uid/stale" "$tmp/calls" || fail "stale dashboard kept"
  ! grep -qxF "DELETE /api/dashboards/uid/a" "$tmp/calls" || fail "deleted a dashboard still in git"
}

test_sync_fails_on_an_error_response() {
  stub_stack 500

  run "$ROOT/scripts/sync-grafana-cloud.sh" "$tmp/dash"
  assert_eq "$status" 1 status
  assert_contains "$err" "reading the data source: HTTP 500" stderr
}
