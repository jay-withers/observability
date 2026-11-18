from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any

from obsview import ops

NOW = datetime(2026, 11, 17, 12, 0, tzinfo=UTC)


def row(hours_ago: float, kind: str, name: str, status: str = "ok", **extra: Any) -> dict[str, Any]:
    base: dict[str, Any] = {
        "TimeGenerated": NOW - timedelta(hours=hours_ago),
        "Kind": kind,
        "Name": name,
        "Status": status,
        "Detail": "",
        "RunUrl": "https://example.invalid/run",
    }
    base.update(extra)
    return base


def build(rows: list[dict[str, Any]], days: int = 7) -> ops.View:
    return ops.build(ops.parse(rows), [], days, NOW)


def test_now_is_each_checks_latest_row() -> None:
    view = build([row(30, "drift", "cloudflare", "drifted"), row(2, "drift", "cloudflare", "ok")])
    assert view.problems == []
    assert view.sections[0].current[0].status == "ok"


def test_rows_older_than_two_days_are_not_now() -> None:
    view = build([row(49, "drift", "cloudflare", "drifted")])
    assert view.problems == []
    assert view.sections == []


def test_problems_match_grafanas_statuses_worst_first() -> None:
    view = build(
        [
            row(1, "heartbeat", "a-job", "overdue"),
            row(1, "drift", "root", "failed"),
            row(1, "drift", "other", "pending"),  # not a problem
            row(1, "heartbeat", "gone", "removed"),  # not a problem
            row(1, "session", "bastion", "running"),
        ]
    )
    assert [(e.name, e.status) for e in view.problems] == [
        ("root", "failed"),
        ("a-job", "overdue"),
        ("bastion", "running"),
    ]


def test_tiles_count_per_kind() -> None:
    view = build(
        [
            row(1, "heartbeat", "a", "overdue"),
            row(1, "heartbeat", "b", "ok"),
            row(1, "drift", "r", "failed"),
            row(1, "expiry", "h", "expiring"),
            row(1, "session", "vm", "ok"),
        ]
    )
    assert {label: n for label, n, _ in view.tiles} == {
        "Jobs overdue": 1,
        "Roots drifted": 1,
        "Certificates expiring": 1,
        "Sessions running": 0,
    }


def test_removed_sinks_below_ok_within_a_section() -> None:
    view = build(
        [
            row(1, "heartbeat", "a", "removed"),
            row(1, "heartbeat", "b", "ok"),
            row(1, "heartbeat", "c", "overdue"),
        ]
    )
    assert [e.name for e in view.sections[0].current] == ["c", "b", "a"]


def test_grid_keeps_the_worst_status_of_each_day() -> None:
    rows = [
        row(1, "drift", "root", "ok"),
        row(3, "drift", "root", "failed"),  # same day (Nov 17), worse
        row(26, "drift", "root", "drifted"),  # Nov 16
    ]
    view = build(rows)
    grid = view.charts["grids"]["drift"]
    assert grid["names"] == ["root"]
    assert view.charts["dates"][-1] == "2026-11-17"
    by_day = {cell[0]: cell[3] for cell in grid["cells"]}
    assert by_day == {6: "failed", 5: "drifted"}


def test_grid_is_capped_at_thirty_days() -> None:
    view = build([row(1, "drift", "root")], days=90)
    assert len(view.charts["dates"]) == ops.GRID_DAYS
    assert ops.window(90) == timedelta(days=ops.GRID_DAYS)
    assert ops.window(7) == timedelta(days=7)


def test_certificates_parse_days_left_and_sort_soonest_first() -> None:
    view = build(
        [
            row(1, "expiry", "a.example", "ok", Detail="Certificate expires 2027-01-30 (74 days)."),
            row(
                1,
                "expiry",
                "b.example",
                "expiring",
                Detail="Certificate expires 2026-12-01 (14 days).",
            ),
            row(
                1, "expiry", "c.example", "failed", Detail="No TLS certificate from c.example:443."
            ),
        ]
    )
    assert [(c.name, c.days_left) for c in view.certificates] == [
        ("b.example", 14),
        ("a.example", 74),
        ("c.example", None),
    ]
    assert view.certificates[2].width == 0.0


def test_unknown_status_reads_as_neutral_not_ok() -> None:
    (event,) = ops.parse([row(1, "drift", "root", "something-new")])
    assert event.severity == ops.NEUTRAL
    assert not event.problem


def test_failed_runs_parse_newest_first() -> None:
    runs = ops.parse_failed_runs(
        [
            {
                "Job": "a",
                "Reason": "StartError",
                "Failures": 2,
                "LastFailure": NOW - timedelta(days=2),
                "Sample": "x",
            },
            {
                "Job": "b",
                "Reason": "BackoffLimitExceeded",
                "Failures": 1,
                "LastFailure": "2026-11-17T10:00:00Z",
            },
        ]
    )
    assert [r.job for r in runs] == ["b", "a"]


def test_kql_filters_the_environment() -> None:
    assert 'Environment == "dev"' in ops.kql("dev")


def test_section_badge_takes_the_worst_problem() -> None:
    view = build([row(1, "heartbeat", "a", "overdue"), row(1, "heartbeat", "b", "ok")])
    assert view.sections[0].worst == "overdue"
    view = build([row(1, "drift", "a", "drifted"), row(1, "drift", "b", "failed")])
    assert view.sections[0].worst == "failed"
    assert build([row(1, "drift", "a", "ok")]).sections[0].worst == "ok"


def test_expiry_is_drawn_as_gauges_so_has_no_grid() -> None:
    view = build([row(1, "expiry", "a.example", Detail="(40 days)"), row(1, "drift", "r")])
    assert set(view.charts["grids"]) == {"drift"}


def test_failed_runs_query_reads_fuzzily() -> None:
    assert "isfuzzy=true" in ops.FAILED_RUNS_KQL


def test_malformed_time_skips_the_row() -> None:
    assert ops.parse([row(1, "drift", "r", TimeGenerated="yesterday")]) == []
