#!/usr/bin/env bash
# Pushes the data source and dashboards in grafana/ to the Grafana Cloud
# stack, so what the stack shows is what is in git. For cd-grafana-cloud.yml,
# and runnable locally with the same environment:
#
#   sync-grafana-cloud.sh [dashboards-dir]      (default: grafana/dashboards)
#
# Needs, from the `dev` environment:
#   GRAFANA_URL                    the stack, e.g. https://jaywithers.grafana.net
#   GRAFANA_TOKEN                  a stack service account token, role Admin
#                                  (Editor cannot write data sources)
#   AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID
#   GRAFANA_AZURE_CLIENT_ID        the app registration Grafana Cloud reads
#   GRAFANA_AZURE_CLIENT_SECRET    Log Analytics as (see README)
#
# Grafana Cloud runs outside Azure, so it cannot use a managed identity: the
# Azure Monitor data source authenticates with that app registration's secret
# instead. The data source keeps uid `azure-monitor`, which every panel names.
#
# Dashboards go into the Platform folder (uid `platform`) with their
# __WORKSPACE_RESOURCE_ID__ filled in, and any dashboard in that folder that
# is no longer in git is deleted. Edits made in the UI are overwritten on the
# next sync: change the JSON here instead.
#
# Tokens and the client secret go to curl through files, never on its command
# line, where any process on the runner could read them.
set -euo pipefail

dir="${1:-grafana/dashboards}"

for v in GRAFANA_URL GRAFANA_TOKEN AZURE_TENANT_ID AZURE_SUBSCRIPTION_ID \
  GRAFANA_AZURE_CLIENT_ID GRAFANA_AZURE_CLIENT_SECRET; do
  if [[ -z "${!v:-}" ]]; then
    echo "sync-grafana-cloud: $v is not set." >&2
    exit 1
  fi
done

# The platform's workspace, by the same names terraform/variables.optional.tf
# defaults to. Built rather than looked up, so this needs no Azure login.
workspace_id="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${PLATFORM_RESOURCE_GROUP:-rg-platform-dev}/providers/Microsoft.OperationalInsights/workspaces/${PLATFORM_WORKSPACE_NAME:-log-platform-dev}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077
printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$GRAFANA_TOKEN" >"$work/headers"

# api <method> <path> [body-file]: prints the HTTP code; the response is in
# $work/response.
api() {
  local args=(-sS -o "$work/response" -w '%{http_code}' -X "$1" -H "@$work/headers")
  [[ $# -ge 3 ]] && args+=(--data "@$3")
  curl "${args[@]}" "${GRAFANA_URL%/}$2"
}

# expect <code> <what> <allowed codes...>
expect() {
  local code="$1" what="$2"
  shift 2
  for ok in "$@"; do [[ "$code" == "$ok" ]] && return 0; done
  echo "sync-grafana-cloud: $what: HTTP $code: $(cat "$work/response")" >&2
  exit 1
}

# The data source: update if it exists, else create.
jq -n \
  --arg tenant "$AZURE_TENANT_ID" --arg client "$GRAFANA_AZURE_CLIENT_ID" \
  --arg sub "$AZURE_SUBSCRIPTION_ID" --arg secret "$GRAFANA_AZURE_CLIENT_SECRET" \
  '{name: "Azure Monitor", uid: "azure-monitor", type: "grafana-azure-monitor-datasource",
    access: "proxy", isDefault: true,
    jsonData: {azureAuthType: "clientsecret", cloudName: "azuremonitor",
               tenantId: $tenant, clientId: $client, subscriptionId: $sub},
    secureJsonData: {clientSecret: $secret}}' >"$work/datasource.json"
code="$(api GET /api/datasources/uid/azure-monitor)"
expect "$code" "reading the data source" 200 404
if [[ "$code" == 200 ]]; then
  expect "$(api PUT /api/datasources/uid/azure-monitor "$work/datasource.json")" "updating the data source" 200
else
  expect "$(api POST /api/datasources "$work/datasource.json")" "creating the data source" 200
fi
echo "sync-grafana-cloud: data source azure-monitor up to date." >&2

# The folder.
code="$(api GET /api/folders/platform)"
expect "$code" "reading the Platform folder" 200 404
if [[ "$code" == 404 ]]; then
  echo '{"uid": "platform", "title": "Platform"}' >"$work/folder.json"
  expect "$(api POST /api/folders "$work/folder.json")" "creating the Platform folder" 200
fi

# Every dashboard, overwriting whatever the stack holds under its uid.
keep=()
for f in "$dir"/*.json; do
  sed "s|__WORKSPACE_RESOURCE_ID__|${workspace_id}|g" "$f" |
    jq --arg msg "observability ${GITHUB_SHA:-local}" \
      '{dashboard: (. + {id: null}), folderUid: "platform", overwrite: true, message: $msg}' \
      >"$work/dashboard.json"
  uid="$(jq -r .dashboard.uid "$work/dashboard.json")"
  expect "$(api POST /api/dashboards/db "$work/dashboard.json")" "pushing $(basename "$f")" 200
  keep+=("$uid")
  echo "sync-grafana-cloud: dashboard $uid pushed." >&2
done

# Whatever else is in the folder was removed from git.
expect "$(api GET '/api/search?folderUIDs=platform&type=dash-db')" "listing the Platform folder" 200
keep_json="$(printf '%s\n' "${keep[@]}" | jq -R . | jq -s .)"
for uid in $(jq -r --argjson keep "$keep_json" '.[] | select(.uid as $u | $keep | index($u) | not) | .uid' "$work/response"); do
  expect "$(api DELETE "/api/dashboards/uid/$uid")" "deleting $uid" 200
  echo "sync-grafana-cloud: dashboard $uid deleted (not in git)." >&2
done

echo "sync-grafana-cloud: ${#keep[@]} dashboard(s) in sync with $GRAFANA_URL." >&2
