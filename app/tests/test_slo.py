from __future__ import annotations

import re
from datetime import UTC, datetime, timedelta

import pytest

from obsview import apps, deployments, slo
from obsview.logs import Row

NOW = datetime(2026, 11, 20, 12, 0, tzinfo=UTC)
AVAILABILITY = next(o for o in slo.OBJECTIVES if o.key == "availability")
JOBS = next(o for o in slo.OBJECTIVES if o.key == "jobs")


def points(*spec: tuple[float, int, int]) -> list[slo.Point]:
    """(hours ago, good, total) each."""
    return [slo.Point(NOW - timedelta(hours=h), g, t) for h, g, t in spec]


def test_the_latency_threshold_in_the_kql_is_latency_ms() -> None:
    assert f"Ms <= {slo.LATENCY_MS})" in slo.HTTP_KQL
    assert re.findall(r"Ms <= (\d+)", slo.HTTP_KQL) == [str(slo.LATENCY_MS)]


def test_the_window_stays_inside_the_container_apps_logs_retention() -> None:
    assert slo.WINDOW_DAYS <= 30
    # The deploy query the SLO pages share never reaches back less than the window.
    assert deployments.window(1) >= slo.WINDOW


def test_budget_is_what_the_target_allows_and_what_is_left() -> None:
    # 99.5% of 2,000 requests allows 10 bad; 4 bad leaves 60% of it.
    sli = slo.measure(AVAILABILITY, points((1, 996, 1000), (30, 1000, 1000)), NOW)
    assert sli.total == 2000
    assert sli.bad == 4
    assert sli.allowed == pytest.approx(10)
    assert sli.budget_left == pytest.approx(0.6)
    assert sli.ratio == pytest.approx(0.998)
    assert sli.status == "ok"


@pytest.mark.parametrize(
    ("bad", "status"),
    [(0, "ok"), (7, "ok"), (8, "at-risk"), (9, "at-risk"), (10, "exhausted"), (25, "exhausted")],
)
def test_status_follows_the_budget_left(bad: int, status: str) -> None:
    sli = slo.measure(AVAILABILITY, points((2, 2000 - bad, 2000)), NOW)
    assert sli.status == status


def test_an_overspent_budget_goes_negative() -> None:
    sli = slo.measure(AVAILABILITY, points((2, 970, 1000)), NOW)
    # 30 bad against 5 allowed: five budgets over.
    assert sli.budget_left == pytest.approx(-5.0)


def test_events_outside_the_window_are_dropped() -> None:
    old = slo.WINDOW_DAYS * 24 + 1
    sli = slo.measure(AVAILABILITY, points((old, 0, 500), (1, 100, 100)), NOW)
    assert sli.total == 100
    assert sli.status == "ok"


def test_no_events_is_no_data_not_ok() -> None:
    sli = slo.measure(AVAILABILITY, [], NOW)
    assert sli.status == "no-data"
    assert sli.ratio is None
    assert sli.budget_left is None
    assert all(left is None for _, left, _, _ in sli.burndown)


def test_a_failed_query_is_unavailable() -> None:
    assert slo.measure(AVAILABILITY, None, NOW).status == "unavailable"


def test_burn_rates_cover_the_last_day_and_week() -> None:
    # Last day: 2% bad against a 0.5% allowance is 4x. The week adds a clean
    # day of equal size: 1% bad, 2x.
    sli = slo.measure(AVAILABILITY, points((3, 980, 1000), (100, 1000, 1000)), NOW)
    assert sli.burn_day == pytest.approx(4.0)
    assert sli.burn_week == pytest.approx(2.0)
    quiet = slo.measure(AVAILABILITY, points((100, 1000, 1000)), NOW)
    assert quiet.burn_day is None


def test_burndown_is_a_day_a_row_falling_as_bad_events_land() -> None:
    sli = slo.measure(
        JOBS, points((72, 0, 1), (1, 1, 1), *[(24 * d, 1, 1) for d in range(5, 20)]), NOW
    )
    assert len(sli.burndown) == slo.WINDOW_DAYS
    assert sli.burndown[-1][0] == NOW.date().isoformat()
    lefts = [left for _, left, _, _ in sli.burndown]
    assert lefts == sorted(lefts, reverse=True)
    assert lefts[0] == 100
    assert lefts[-1] == pytest.approx(100 * sli.budget_left, abs=0.01)  # type: ignore[operator]
    three_days_ago = (NOW - timedelta(hours=72)).date().isoformat()
    day = next(row for row in sli.burndown if row[0] == three_days_ago)
    assert day[2:] == [1, 1]


def test_job_runs_count_only_finished_runs_and_a_failure_wins() -> None:
    rows: list[Row] = [
        {"Job": "caj-gymlog-dev-backup", "Started": NOW, "Completed": 1, "Failed": 0},
        {"Job": "caj-gymlog-dev-backup", "Started": NOW, "Completed": 1, "Failed": 1},
        {"Job": "caj-gymlog-dev-backup", "Started": NOW, "Completed": 0, "Failed": 0},
        {"Job": "caj-gymlog-dev-backup", "Started": None, "Completed": 1, "Failed": 0},
    ]
    assert [r.ok for r in slo.parse_jobs(rows)] == [True, False]


def test_http_hours_split_into_availability_and_latency() -> None:
    rows = [
        {"Hour": NOW, "Workload": "ca-gymlog-dev", "Total": 10, "Errors": 1, "Timed": 8, "Fast": 6},
        {"Hour": "junk", "Workload": "ca-gymlog-dev", "Total": 10},
    ]
    hours = slo.parse_http(rows)
    assert len(hours) == 1
    assert slo.availability_points(hours) == [slo.Point(NOW, 9, 10)]
    # Latency counts only the requests with a duration logged.
    assert slo.latency_points(hours) == [slo.Point(NOW, 6, 8)]


@pytest.mark.parametrize(
    ("ratio", "text"),
    [
        (None, "–"),
        (1.0, "100%"),
        (0.99999, "99.99%"),
        (0.995, "99.5%"),
        (0.9987, "99.87%"),
        (0.95, "95%"),
        (0.9312, "93.1%"),
        (0.5, "50%"),
    ],
)
def test_ratios_are_truncated_never_rounded_up_to_100(ratio: float | None, text: str) -> None:
    assert slo.fmt_ratio(ratio) == text


def test_an_apps_report_is_its_own_rows_and_worst_status() -> None:
    gym, agent = apps.find("gym-log"), apps.find("repo-agent")
    assert gym is not None and agent is not None
    hours = [
        slo.HttpHour(NOW, "ca-gymlog-dev", 100, 0, 100, 100),
        slo.HttpHour(NOW, "ca-finances-dev", 100, 100, 100, 0),
    ]
    runs = [slo.JobRun(NOW, "caj-gymlog-dev-backup", False)]
    report = apps.slo_report(gym, hours, runs, [], NOW)
    by_key = {s.objective.key: s for s in report.slis}
    assert by_key["availability"].status == "ok"
    assert by_key["jobs"].status == "exhausted"
    assert by_key["deploys"].status == "no-data"
    assert report.status == "exhausted"
    assert report.least_budget is by_key["jobs"]
    # No deploys: measured leaves it out; a job-only app has no request SLIs.
    assert [s.objective.key for s in report.measured] == ["availability", "latency", "jobs"]
    job_only = apps.slo_report(agent, hours, runs, [], NOW)
    assert [s.objective.key for s in job_only.slis] == ["jobs", "deploys"]


def test_a_failed_query_reads_unavailable_not_ok() -> None:
    gym = apps.find("gym-log")
    assert gym is not None
    report = apps.slo_report(gym, None, [], [], NOW)
    assert report.status == "unavailable"
    assert "unavailable" not in report.charts["burndown"]


def test_tally_counts_apps_by_status() -> None:
    reports = [
        slo.Report("a", "A", [slo.Sli(AVAILABILITY, "ok")]),
        slo.Report("b", "B", [slo.Sli(AVAILABILITY, "ok"), slo.Sli(JOBS, "at-risk")]),
        slo.Report("c", "C", []),
    ]
    assert slo.tally(reports) == {
        "exhausted": 0,
        "at-risk": 1,
        "unavailable": 0,
        "ok": 1,
        "no-data": 1,
    }


def test_the_window_is_whole_days_so_the_burndown_ends_on_the_budget_left() -> None:
    # Regression: the window was `now - 28d`, so events on the morning of
    # the 29th day back spent budget the burn-down never showed.
    start = slo.window_start(NOW)
    assert start == datetime(2026, 10, 24, tzinfo=UTC)
    edge = [
        slo.Point(start - timedelta(seconds=1), 0, 100),  # outside
        slo.Point(start, 990, 1000),  # the first hourly bin, counted whole
        slo.Point(NOW - timedelta(hours=1), 1000, 1000),
    ]
    sli = slo.measure(AVAILABILITY, edge, NOW)
    assert sli.total == 2000
    assert sli.burndown[0][2:] == [10, 1000]
    assert sli.burndown[-1][1] == pytest.approx(100 * sli.budget_left, abs=0.01)  # type: ignore[operator]


def test_burn_rates_count_the_hourly_bin_they_start_in() -> None:
    # Regression: the bin starting before `now - 24h` was dropped whole.
    at = NOW.replace(minute=40) - timedelta(days=1)
    bin_start = at.replace(minute=0)
    sli = slo.measure(AVAILABILITY, [slo.Point(bin_start, 0, 10)], NOW.replace(minute=40))
    assert sli.burn_day is not None


def test_a_job_run_counts_in_the_window_it_was_created_in() -> None:
    # Regression: `Started` was the first event in the window, so a run
    # created before it was dated by its completion and counted.
    assert 'Started = minif(TimeGenerated, Reason_s == "SuccessfulCreate")' in slo.JOBS_KQL
    rows: list[Row] = [{"Job": "caj-x-dev-y", "Started": None, "Completed": 1, "Failed": 0}]
    assert slo.parse_jobs(rows) == []


@pytest.mark.parametrize(
    ("left", "text"),
    [
        (1.0, "100%"),
        (0.476, "47%"),
        (0.0, "0%"),
        (0.004, "0.4%"),
        (0.0004, "<0.1%"),
        (-0.004, "0.4%"),
        (-1.5, "150%"),
    ],
)
def test_budgets_are_truncated_and_never_read_as_spent_when_they_are_not(
    left: float, text: str
) -> None:
    # Regression: rounded to whole percent, 0.4% left read "0% left" beside
    # an "at risk" badge.
    assert slo.fmt_budget(left) == text
