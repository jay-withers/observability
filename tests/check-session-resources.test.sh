# shellcheck shell=bash
# shellcheck disable=SC2154 # tmp, out, err, status: set by lib.sh's setup and run
# scripts/check-session-resources.sh against a stubbed Resource Graph answer.
# The billing rules and the session-until tag live in the script's jq, so they
# are tested here; the KQL's type-to-category mapping is not.

sessions=("$ROOT/scripts/check-session-resources.sh" sub-123 https://run.example/1)

# stub_az <jq-array>: answers with these rows, already categorised as the KQL
# would project them. A jq expression rather than JSON, so the fixtures read
# without quoted keys. Missing fields default to empty.
stub_az() {
  jq -cn "($1) as \$rows"' | {data: [$rows[]
    | {Category: "", name: "", resourceGroup: "rg", Until: "", AksPower: "",
       VmPower: "", Sku: "", Profiles: null} + .]}' >"$tmp/result.json"
  stub az <<SH
cat "$tmp/result.json"
SH
}

# row <category>: that category's row from the last run, as Status|Detail.
row() {
  field ".[] | select(.Name == \"$1\") | \"\(.Status)|\(.Detail)\""
}

test_sessions_usage() {
  run "$ROOT/scripts/check-session-resources.sh"
  assert_eq "$status" 2 status
}

test_sessions_every_category_reports_when_nothing_runs() {
  stub_az '[]'

  run "${sessions[@]}"
  assert_eq "$status" 0 status
  assert_eq "$(field '[.[].Name] | join(",")')" \
    firewall,aks,vm,bastion,vpn-gateway,app-gateway,nat-gateway,workload-profile categories
  assert_eq "$(field '[.[] | "\(.Status)|\(.Detail)"] | unique | join(",")')" "ok|Nothing running." "every row"
  assert_eq "$(field '[.[] | .Kind, .Repo, .RunUrl] | unique | join(",")')" \
    "https://run.example/1,observability,session" "Kind, Repo and RunUrl"
}

test_sessions_firewall_bills_while_it_exists() {
  stub_az '[{Category: "firewall", name: "afw-hub", resourceGroup: "rg-hub"}]'

  run "${sessions[@]}"
  assert_eq "$(row firewall)" \
    "running|Still running: rg-hub/afw-hub. Tag session-until=YYYY-MM-DD to keep it." firewall
}

test_sessions_aks_bills_only_when_running() {
  stub_az '[{Category: "aks", name: "aks-on", AksPower: "Running"},
            {Category: "aks", name: "aks-off", AksPower: "Stopped"}]'

  run "${sessions[@]}"
  assert_eq "$(row aks)" "running|Still running: rg/aks-on. Tag session-until=YYYY-MM-DD to keep it." aks
}

test_sessions_vm_bills_unless_deallocated() {
  stub_az '[{Category: "vm", name: "vm-dealloc", VmPower: "PowerState/deallocated"},
            {Category: "vm", name: "vm-stopped", VmPower: "PowerState/stopped"}]'

  run "${sessions[@]}"
  assert_eq "$(row vm)" "running|Still running: rg/vm-stopped. Tag session-until=YYYY-MM-DD to keep it." \
    "vm (stopped in the guest still bills)"
}

test_sessions_bastion_developer_sku_is_free() {
  stub_az '[{Category: "bastion", name: "bas-dev", Sku: "Developer"}]'
  run "${sessions[@]}"
  assert_eq "$(row bastion)" "ok|Nothing running." "Developer bastion"

  stub_az '[{Category: "bastion", name: "bas-basic", Sku: "Basic"}]'
  run "${sessions[@]}"
  assert_eq "$(field '.[] | select(.Name == "bastion") | .Status')" running "Basic bastion"
}

test_sessions_workload_profile_bills_beyond_consumption() {
  stub_az '[{Category: "workload-profile", name: "cae-consumption",
             Profiles: [{workloadProfileType: "Consumption"}]},
            {Category: "workload-profile", name: "cae-none"},
            {Category: "workload-profile", name: "cae-dedicated",
             Profiles: [{workloadProfileType: "Consumption"}, {workloadProfileType: "D4"}]}]'

  run "${sessions[@]}"
  assert_eq "$(row workload-profile)" \
    "running|Still running: rg/cae-dedicated. Tag session-until=YYYY-MM-DD to keep it." workload-profile
}

test_sessions_until_tag_keeps_through_that_day() {
  today="$(date -u +%Y-%m-%d)"
  yesterday="$(date -u -d yesterday +%Y-%m-%d)"
  stub_az "[{Category: \"firewall\", name: \"afw-kept\", Until: \"$today\"},
            {Category: \"vm\", name: \"vm-lapsed\", VmPower: \"PowerState/running\", Until: \"$yesterday\"}]"

  run "${sessions[@]}"
  assert_eq "$(row firewall)" "ok|Kept on purpose: rg/afw-kept until $today." "firewall tagged until today"
  assert_eq "$(field '.[] | select(.Name == "vm") | .Status')" running "vm tagged until yesterday"
}

test_sessions_running_and_kept_in_one_category() {
  today="$(date -u +%Y-%m-%d)"
  stub_az "[{Category: \"firewall\", name: \"afw-left\"},
            {Category: \"firewall\", name: \"afw-kept\", Until: \"$today\"}]"

  run "${sessions[@]}"
  assert_eq "$(row firewall)" \
    "running|Still running: rg/afw-left. Tag session-until=YYYY-MM-DD to keep it. Kept on purpose: rg/afw-kept until $today." \
    firewall
}
