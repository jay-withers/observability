from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any

from obsview import apps, costs, deployments, ops

NOW = datetime(2026, 11, 17, 12, 0, tzinfo=UTC)
GYM = apps.find("health")
assert GYM is not None


def event(kind: str, name: str, status: str = "ok") -> dict[str, Any]:
    return {"TimeGenerated": NOW, "Kind": kind, "Name": name, "Status": status, "Detail": ""}


def names(rows: list[dict[str, Any]]) -> list[str]:
    assert GYM is not None
    return [e.name for e in apps.events_for(GYM, ops.parse(rows))]


def test_every_slug_is_found_and_unknown_is_none() -> None:
    assert all(apps.find(a.slug) is a for a in apps.APPS)
    assert apps.find("nope") is None


def test_workloads_match_on_the_container_apps_naming() -> None:
    assert GYM is not None
    assert GYM.owns_workload("ca-gymlog-dev")
    assert GYM.owns_workload("caj-gymlog-dev-backup")
    assert GYM.owns_workload("CAJ-GYMLOG-DEV-GARMIN")
    assert not GYM.owns_workload("gymlog-dev")
    assert not GYM.owns_workload("ca-gymlogx-dev")


def test_one_apps_prefix_never_matches_anothers() -> None:
    for a in apps.APPS:
        for b in apps.APPS:
            if a is not b:
                assert not a.owns_workload(f"caj-{b.prefix}-dev-job"), (a.slug, b.slug)
                assert not a.owns_root(f"{b.slug}/terraform"), (a.slug, b.slug)


def test_ops_events_by_root_job_check_and_host() -> None:
    rows = [
        event("drift", "health/terraform"),
        event("drift", "health-extra/terraform"),
        event("drift", "azure-landingzone/hub"),
        event("heartbeat", "caj-gymlog-dev-backup"),
        event("heartbeat", "caj-finances-dev-backup"),
        event("heartbeat", "check: drift gym-log/terraform"),
        event("heartbeat", "check: expiry"),
        event("heartbeat", "Deployments_CL"),
        event("expiry", "health.jaywithers.uk"),
        event("expiry", "finances.jaywithers.uk"),
        event("session", "bastion"),
        event("cost-feed", "Costs_CL"),
    ]
    assert names(rows) == [
        "health/terraform",
        "caj-gymlog-dev-backup",
        "check: drift gym-log/terraform",
        "health.jaywithers.uk",
    ]


def test_failed_runs_and_crashes_by_workload() -> None:
    assert GYM is not None
    runs = [
        ops.FailedRun("caj-gymlog-dev-garmin", "BackoffLimitExceeded", 1, NOW, ""),
        ops.FailedRun("ca-gymlog-dev", "ContainerCrashing", 2, NOW, ""),
        ops.FailedRun("caj-sectest-dev-zap", "BackoffLimitExceeded", 1, NOW, ""),
    ]
    mine = apps.failed_runs_for(GYM, runs)
    assert mine is not None and [r.job for r in mine] == ["caj-gymlog-dev-garmin", "ca-gymlog-dev"]
    assert apps.failed_runs_for(GYM, None) is None


def test_costs_by_resource_group() -> None:
    assert GYM is not None
    rows = [
        {"UsageDate": NOW, "ResourceGroup": "RG-GYMLOG-DEV", "ServiceName": "s", "Cost": 1.0},
        {"UsageDate": NOW, "ResourceGroup": "rg-platform-dev", "ServiceName": "s", "Cost": 9.0},
    ]
    assert [c.cost for c in apps.costs_for(GYM, costs.parse(rows))] == [1.0]


def test_summary_counts_the_range_and_says_when_a_query_failed() -> None:
    assert GYM is not None
    deploys = deployments.parse(
        [
            {"TimeGenerated": NOW - timedelta(hours=1), "Repo": "health", "Outcome": "success"},
            {"TimeGenerated": NOW - timedelta(hours=2), "Repo": "health", "Outcome": "failed"},
            {"TimeGenerated": NOW - timedelta(days=9), "Repo": "health", "Outcome": "success"},
            {"TimeGenerated": NOW, "Repo": "finances", "Outcome": "success"},
        ]
    )
    current = list(
        ops.latest(
            ops.parse([event("heartbeat", "caj-gymlog-dev-backup", "overdue")]), NOW
        ).values()
    )
    totals = {"ca-gymlog-dev": 4, "caj-gymlog-dev-backup": 1, "ca-career-dev": 9}
    since = deployments.range_start(7, NOW)
    assert since == datetime(2026, 11, 11, tzinfo=UTC)
    s = apps.summarise(GYM, deploys, since, current, 1.5, totals)
    assert (s.deploys, s.success_pct, s.problems, s.spend, s.errors) == (2, 50.0, 1, 1.5, 5)
    assert s.last is not None and s.last.outcome == "success"
    s = apps.summarise(GYM, deploys, since, None, None, None)
    assert (s.problems, s.spend, s.errors) == (None, None, None)


# --- the platform ---------------------------------------------------------------


def test_platform_events_are_those_no_app_owns() -> None:
    rows = [
        event("drift", "azure-landingzone/hub"),
        event("drift", "cloudflare/terraform"),
        event("drift", "health/terraform"),
        event("heartbeat", "caj-gymlog-dev-backup"),
        event("heartbeat", "check: drift azure-container-apps/terraform"),
        event("heartbeat", "check: drift finances/terraform"),
        event("expiry", "health.jaywithers.uk"),
        event("expiry", "example.jaywithers.uk"),
        event("session", "firewall"),
        event("cost-feed", "Costs_CL"),
    ]
    got = [e.name for e in apps.platform_events(ops.parse(rows))]
    assert got == [
        "azure-landingzone/hub",
        "cloudflare/terraform",
        "check: drift azure-container-apps/terraform",
        "example.jaywithers.uk",
        "firewall",
        "Costs_CL",
    ]


def test_every_event_is_an_apps_or_the_platforms_never_both() -> None:
    rows = [event("drift", f"{a.slug}/terraform") for a in apps.APPS]
    rows += [event("heartbeat", f"caj-{a.prefix}-dev-job") for a in apps.APPS]
    rows += [event("drift", "azure-landingzone/hub"), event("session", "vm")]
    parsed = ops.parse(rows)
    platform = apps.platform_events(parsed)
    for e in parsed:
        owners = [a for a in apps.APPS if apps.owns_event(a, e)]
        assert len(owners) + (e in platform) == 1, e.name


def test_platform_failed_runs_leave_out_app_workloads() -> None:
    assert apps.platform_failed_runs(None) is None
    runs = [
        ops.FailedRun(job, "Error", 1, NOW, "")
        for job in ("caj-gymlog-dev-backup", "ca-finances-dev", "caj-platform-dev-sweep")
    ]
    assert [r.job for r in apps.platform_failed_runs(runs) or []] == ["caj-platform-dev-sweep"]


def test_platform_costs_are_the_groups_no_app_owns() -> None:
    rows = costs.parse(
        [
            {
                "UsageDate": NOW,
                "ResourceGroup": rg,
                "ServiceName": "s",
                "Cost": 1.0,
                "Currency": "USD",
            }
            for rg in ("rg-platform-dev", "RG-GYMLOG-DEV", "rg-aks-dev", "rg-hub-dev")
        ]
    )
    assert [c.resource_group for c in apps.platform_costs(rows)] == [
        "rg-platform-dev",
        "rg-aks-dev",
        "rg-hub-dev",
    ]
    assert GYM is not None
    assert [c.resource_group for c in apps.costs_for(GYM, rows)] == ["rg-gymlog-dev"]


def test_a_renamed_repos_rows_stay_with_its_app() -> None:
    """health was gym-log until 2026-11-21, and rows from before then say so.

    Without `former`, its old drift rows would be unowned, and unowned is the
    platform's, so they'd show up on the Platform page as someone else's
    problem.
    """
    assert GYM is not None
    assert apps.find("gym-log") is GYM
    assert GYM.owns_root("gym-log/terraform")
    assert not GYM.owns_root("gym-log-extra/terraform")
    drift = ops.parse([event("drift", "gym-log/terraform", "drifted")])
    assert apps.platform_events(drift) == []


def test_no_apps_former_name_is_anothers() -> None:
    names = [r for a in apps.APPS for r in a.repos]
    assert len(names) == len(set(names))


def test_a_former_names_deploys_read_as_the_app_so_they_make_one_lane() -> None:
    """The deploy workflow's `App` defaults to the repo, so old rows say gym-log twice."""
    assert GYM is not None
    rows = [
        {"TimeGenerated": NOW, "Repo": "health", "App": "health", "Outcome": "success"},
        {
            "TimeGenerated": NOW - timedelta(days=1),
            "Repo": "gym-log",
            "App": "gym-log",
            "Outcome": "failed",
        },
        {"TimeGenerated": NOW - timedelta(days=2), "Repo": "gym-log", "Outcome": "success"},
        # An App named for something else is kept, as `label` shows it.
        {
            "TimeGenerated": NOW - timedelta(days=3),
            "Repo": "gym-log",
            "App": "worker",
            "Outcome": "success",
        },
        {"TimeGenerated": NOW, "Repo": "finances", "Outcome": "success"},
    ]
    mine = apps.deploys_for(GYM, apps.parse_deploys(rows))
    assert [d.label for d in mine] == ["health", "health", "health", "health / worker"]
    view = deployments.build(mine, 7, NOW)
    assert [a.label for a in view.apps] == ["health", "health / worker"]
