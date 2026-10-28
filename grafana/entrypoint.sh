#!/bin/sh
# Renders the dashboards with this deployment's workspace, then starts Grafana.
#
# Every Log Analytics panel names the workspace it queries by resource ID,
# and Grafana substitutes environment variables in provisioning YAML but not
# in dashboard JSON. So the JSON in git carries __WORKSPACE_RESOURCE_ID__,
# and this fills it in from WORKSPACE_RESOURCE_ID (set by the Terraform) into
# the directory the dashboard provider reads.
set -eu

: "${WORKSPACE_RESOURCE_ID:?WORKSPACE_RESOURCE_ID must be set}"

src=/etc/grafana/dashboards
dst=/var/lib/grafana/dashboards
mkdir -p "$dst"
for f in "$src"/*.json; do
  sed "s|__WORKSPACE_RESOURCE_ID__|${WORKSPACE_RESOURCE_ID}|g" "$f" > "$dst/$(basename "$f")"
done

exec /run.sh "$@"
