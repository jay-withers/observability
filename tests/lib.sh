# shellcheck shell=bash
# Helpers for tests/*.test.sh, sourced by tests/run.sh. Every test runs in its
# own subshell under `set -e`, so a failed assertion ends that test only.

# A scratch directory per test, removed when the test's subshell exits, with
# bin/ first on PATH.
setup() {
  tmp="$(mktemp -d)"
  # shellcheck disable=SC2064 # expand now: $tmp is this test's directory
  trap "rm -rf '$tmp'" EXIT
  mkdir "$tmp/bin"
  PATH="$tmp/bin:$PATH"
}

# stub <name> <<'SH' ... SH: a bash script on PATH that shadows <name>.
# Use an unquoted heredoc to bake the test's $tmp into the stub.
stub() {
  {
    echo '#!/usr/bin/env bash'
    cat
  } >"$tmp/bin/$1"
  chmod +x "$tmp/bin/$1"
}

# run <command...>: sets $out, $err and $status rather than failing the test.
# shellcheck disable=SC2034 # status and err are read by the tests
run() {
  set +e
  out="$("$@" 2>"$tmp/stderr")"
  status=$?
  set -e
  # The scripts summarise to stderr through `tee >(...)`, which can finish
  # after the script does. Give it a moment before reading.
  wait 2>/dev/null || true
  err="$(cat "$tmp/stderr")"
}

fail() {
  echo "    $*" >&2
  return 1
}

# assert_eq <actual> <expected> [what]
assert_eq() {
  [[ "$1" == "$2" ]] || fail "${3:-value}: expected [$2], got [$1]"
}

# assert_contains <haystack> <needle> [what]
assert_contains() {
  [[ "$1" == *"$2"* ]] || fail "${3:-output}: expected to contain [$2], got [$1]"
}

# field <jq-filter>: applies a jq filter to the last run's stdout.
field() {
  jq -r "$1" <<<"$out"
}
