# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-expiry.sh against a stubbed `openssl`. A host with a file under
# $tmp/certs gets a certificate ending on the date in it; any other host fails
# the handshake.

stub_openssl() {
  mkdir -p "$tmp/certs"
  stub openssl <<SH
case "\$1" in
  s_client)
    while ((\$#)); do
      if [[ "\$1" == -servername ]]; then echo "\$2"; fi
      shift
    done
    ;;
  x509)
    read -r host || true
    [[ -n "\$host" && -f "$tmp/certs/\$host" ]] || exit 1
    echo "notAfter=\$(cat "$tmp/certs/\$host")"
    ;;
esac
SH
}

# cert <host> <days-from-now>
cert() {
  date -u -d "+$2 days" '+%b %e %H:%M:%S %Y GMT' >"$tmp/certs/$1"
}

hosts_file() {
  cat >"$tmp/hosts" <<'EOF'
# Every custom domain on the estate.
good.example

soon.example   # trailing comment
down.example
EOF
}

test_expiry_usage() {
  run "$ROOT/scripts/check-expiry.sh"
  assert_eq "$status" 2 status
}

test_expiry_statuses() {
  stub_openssl
  cert good.example 90
  cert soon.example 5
  hosts_file

  run "$ROOT/scripts/check-expiry.sh" "$tmp/hosts" https://run.example/1
  assert_eq "$status" 0 status
  assert_eq "$(field '[.[].Name] | join(",")')" good.example,soon.example,down.example \
    "hosts, with comments and blank lines skipped"
  assert_eq "$(field '[.[].Status] | join(",")')" ok,expiring,failed statuses
  assert_contains "$(field '.[0].Detail')" "Certificate expires $(date -u -d '+90 days' +%Y-%m-%d)" Detail
  assert_eq "$(field '.[2].Detail')" "No TLS certificate from down.example:443." "failed Detail"
  assert_eq "$(field '[.[] | .Kind, .Repo, .RunUrl] | unique | join(",")')" \
    "expiry,https://run.example/1,observability" "Kind, Repo and RunUrl"
}

test_expiry_warn_days_is_configurable() {
  stub_openssl
  cert soon.example 5
  echo soon.example >"$tmp/hosts"

  EXPIRY_WARN_DAYS=3 run "$ROOT/scripts/check-expiry.sh" "$tmp/hosts"
  assert_eq "$(field '.[0].Status')" ok "status with a 3-day warning"
}
