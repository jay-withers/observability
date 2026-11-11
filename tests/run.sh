#!/usr/bin/env bash
# Runs every tests/*.test.sh: each test_* function in its own subshell, with a
# fresh scratch directory whose bin/ is first on PATH, so the stubs a test
# writes there (az, curl, openssl, sleep) stand in for the real thing.
#
#   tests/run.sh          (or `make test`; pre-commit runs it too)
#
# No framework to install: bash and jq, which the scripts need anyway, so the
# same run works locally (jq from mise.toml) and on the pre-commit runner.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export ROOT
# shellcheck source=tests/lib.sh
source "$ROOT/tests/lib.sh"

passed=0 failed=0
for file in "$ROOT"/tests/*.test.sh; do
  # shellcheck source=/dev/null
  source "$file"
  for t in $(declare -F | awk '$3 ~ /^test_/ {print $3}'); do
    set +e
    (set -e; setup; "$t")
    rc=$?
    set -e
    if ((rc == 0)); then
      passed=$((passed + 1))
      echo "ok   $(basename "$file") $t"
    else
      failed=$((failed + 1))
      echo "FAIL $(basename "$file") $t"
    fi
    unset -f "$t"
  done
done

echo "$passed passed, $failed failed"
((failed == 0))
