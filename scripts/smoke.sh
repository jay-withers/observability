#!/usr/bin/env bash
# Proves a Grafana image tag is live: `make smoke` after `make deploy`, locally
# or in the shared container-apps-deploy.yml, which rolls back when this fails.
#
#   smoke.sh <tag> <resource-group> <image-without-tag> <app-name>
#
# Not the app repos' shared smoke.sh: that one needs a /healthz reporting the
# release tag, which Grafana cannot serve. Instead, for the app:
#
#   - the latest revision is configured with the image;
#   - the latest ready revision is that latest revision, so the new
#     container actually started (Grafana's probes hit /api/health);
#   - /api/health answers 200 with `"database": "ok"` on the default FQDN,
#     retrying through a cold start: Grafana is up *and* reached its Postgres.
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <tag> <resource-group> <image-without-tag> <app-name>" >&2
  exit 2
fi
tag="$1" rg="$2" image="$3:$1" name="$4"
timeout="${SMOKE_TIMEOUT:-300}"

fail() {
  echo "smoke: FAIL: $*" >&2
  exit 1
}

app="$(az containerapp show -g "$rg" -n "$name" -o json)"
configured="$(jq -r '.properties.template.containers[0].image' <<<"$app")"
[[ "$configured" == "$image" ]] || fail "$name is configured with $configured, not $image"
fqdn="$(jq -r '.properties.configuration.ingress.fqdn // empty' <<<"$app")"
[[ -n "$fqdn" ]] || fail "$name has no ingress FQDN"
latest="$(jq -r '.properties.latestRevisionName' <<<"$app")"

deadline=$((SECONDS + timeout))
while :; do
  code="$(curl -sS -o /tmp/smoke-body.$$ -w '%{http_code}' --max-time 30 "https://$fqdn/api/health" || true)"
  body="$(cat /tmp/smoke-body.$$ 2>/dev/null || true)"
  if [[ "$code" == 200 && "$(jq -r '.database // empty' <<<"$body" 2>/dev/null)" == ok ]]; then
    ready="$(az containerapp show -g "$rg" -n "$name" --query properties.latestReadyRevisionName -o tsv)"
    [[ "$ready" == "$latest" ]] && break
  fi
  ((SECONDS < deadline)) || fail "$name: $tag not healthy within ${timeout}s (last: HTTP $code ${body:0:200})"
  echo "smoke: $name: waiting (HTTP $code)"
  sleep 10
done
rm -f /tmp/smoke-body.$$
echo "smoke: $name: $tag live, Grafana $(jq -r .version <<<"$body"), database ok"
