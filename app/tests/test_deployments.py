from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any

from obsview import deployments as dp

NOW = datetime(2026, 11, 17, 12, 0, tzinfo=UTC)


def row(hours_ago: float, **overrides: Any) -> dict[str, Any]:
    base: dict[str, Any] = {
        "TimeGenerated": NOW - timedelta(hours=hours_ago),
        "Repo": "gym-log",
        "App": "gymlog",
        "Version": "v1.0.1",
        "PreviousVersion": "v1.0.0",
        "Outcome": "success",
        "Trigger": "renovate",
        "LeadTimeSec": 600,
        "CommitSha": "abcdef1234567",
        "RunUrl": "https://example.invalid/run",
    }
    base.update(overrides)
    return base


def kpi(view: dp.View, label: str) -> dp.Kpi:
    return next(k for k in view.kpis if k.label == label)


def test_parse_accepts_strings_and_naive_times_and_sorts_newest_first() -> None:
    deploys = dp.parse(
        [
            row(5, TimeGenerated="2026-11-17T07:00:00Z"),
            row(1, TimeGenerated=datetime(2026, 11, 17, 11, 0)),
            {"TimeGenerated": None},
        ]
    )
    assert [d.when.hour for d in deploys] == [11, 7]
    assert all(d.when.tzinfo is not None for d in deploys)
    assert deploys[0].commit == "abcdef1"


def test_label_omits_app_named_after_repo() -> None:
    a, b = dp.parse([row(1, App="gymlog"), row(2, Repo="finances", App="finances")])
    assert a.label == "gym-log / gymlog"
    assert b.label == "finances"


def test_untested_counts_as_success_like_the_grafana_panel() -> None:
    view = dp.build(
        dp.parse(
            [
                row(1),
                row(2, Outcome="untested"),
                row(3, Outcome="failed"),
                row(4, Outcome="rolled_back"),
            ]
        ),
        days=7,
        now=NOW,
    )
    assert kpi(view, "Success rate").value == "50.0%"
    assert kpi(view, "Failed").value == "1"
    assert kpi(view, "Rolled back").value == "1"
    assert view.total == 4


def test_previous_period_drives_the_deltas_and_their_tone() -> None:
    rows = [row(24), row(48)]  # two this week
    rows += [row(24 * 8, Outcome="failed"), row(24 * 9), row(24 * 10)]  # three the week before
    view = dp.build(dp.parse(rows), days=7, now=NOW)
    deploys = kpi(view, "Deploys")
    assert deploys.delta == "-1 vs prior 7d"
    assert deploys.tone == "neutral"  # fewer deploys is neither good nor bad
    failed = kpi(view, "Failed")
    assert failed.delta == "-1 vs prior 7d"
    assert failed.tone == "good"
    rate = kpi(view, "Success rate")
    assert rate.delta == "+33.3 pts vs prior 7d"
    assert rate.tone == "good"


def test_no_previous_period_means_no_rate_delta() -> None:
    view = dp.build(dp.parse([row(1)]), days=7, now=NOW)
    assert kpi(view, "Success rate").delta is None
    assert kpi(view, "Median lead time").delta is None


def test_rows_outside_the_range_are_not_counted() -> None:
    view = dp.build(dp.parse([row(1), row(24 * 8)]), days=7, now=NOW)
    assert view.total == 1
    assert sum(sum(v) for v in view.charts["outcomes"].values()) == 1


def test_median_lead_time_ignores_missing_values() -> None:
    view = dp.build(
        dp.parse([row(1, LeadTimeSec=60), row(2, LeadTimeSec=None), row(3, LeadTimeSec=300)]),
        days=7,
        now=NOW,
    )
    assert kpi(view, "Median lead time").value == "3m"


def test_daily_buckets_span_the_range_ending_today() -> None:
    view = dp.build(dp.parse([row(1), row(2), row(30)]), days=7, now=NOW)
    assert len(view.charts["dates"]) == 7
    assert view.charts["dates"][-1] == "2026-11-17"
    assert view.charts["outcomes"]["success"][-1] == 2
    assert view.charts["outcomes"]["success"][-2] == 1


def test_triggers_are_counted_most_common_first() -> None:
    view = dp.build(dp.parse([row(1), row(2), row(3, Trigger="merge")]), days=7, now=NOW)
    assert view.charts["triggers"] == [
        {"name": "renovate", "value": 2},
        {"name": "merge", "value": 1},
    ]


def test_calendar_always_covers_thirteen_weeks() -> None:
    view = dp.build(dp.parse([row(1), row(24 * 60)]), days=7, now=NOW)
    cal = view.charts["calendar"]
    assert len(cal["data"]) == dp.CALENDAR_DAYS
    assert cal["end"] == "2026-11-17"
    # The 60-day-old deploy is outside the 7-day range but on the calendar.
    assert sum(n for _, n in cal["data"]) == 2


def test_window_reaches_back_far_enough_for_deltas_and_calendar() -> None:
    assert dp.window(7) == timedelta(days=dp.CALENDAR_DAYS)
    assert dp.window(90) == timedelta(days=180)


def test_timeline_puts_the_busiest_app_on_top() -> None:
    view = dp.build(
        dp.parse([row(1), row(2), row(3, Repo="finances", App="finances")]), days=7, now=NOW
    )
    timeline = view.charts["timeline"]
    # ECharts draws the first category at the bottom.
    assert timeline["apps"] == ["finances", "gym-log / gymlog"]
    assert len(timeline["points"]) == 3
    assert timeline["points"][0][0] < timeline["points"][-1][0]  # oldest first


def test_lead_histogram_buckets() -> None:
    view = dp.build(
        dp.parse([row(i + 1, LeadTimeSec=s) for i, s in enumerate([60, 299, 300, 1500, 9999])]),
        days=7,
        now=NOW,
    )
    assert view.charts["lead"]["labels"] == ["0–5m", "5–10m", "10–20m", "20–40m", "40m+"]
    assert view.charts["lead"]["counts"] == [2, 1, 0, 1, 1]


def test_apps_sorted_by_last_deploy_with_last_outcome() -> None:
    view = dp.build(
        dp.parse(
            [
                row(1, Repo="finances", App="finances", Outcome="failed"),
                row(5),
                row(6, Outcome="rolled_back"),
            ]
        ),
        days=7,
        now=NOW,
    )
    assert [a.label for a in view.apps] == ["finances", "gym-log / gymlog"]
    assert view.apps[0].last.outcome == "failed"
    gym = view.apps[1]
    assert (gym.deploys, gym.rolled_back, gym.success_pct) == (2, 1, 50.0)


def test_fmt_duration() -> None:
    assert dp.fmt_duration(42) == "42s"
    assert dp.fmt_duration(125) == "2m 05s"
    assert dp.fmt_duration(600) == "10m"
    assert dp.fmt_duration(3900) == "1h 05m"


def test_fmt_ago() -> None:
    assert dp.fmt_ago(NOW - timedelta(seconds=30), NOW) == "just now"
    assert dp.fmt_ago(NOW - timedelta(minutes=5), NOW) == "5m ago"
    assert dp.fmt_ago(NOW - timedelta(hours=3), NOW) == "3h ago"
    assert dp.fmt_ago(NOW - timedelta(days=2), NOW) == "2d ago"


def test_kql_filters_the_environment_and_leaves_the_window_to_timespan() -> None:
    query = dp.kql("dev")
    assert 'Environment == "dev"' in query
    assert "ago(" not in query


def test_tiles_and_charts_count_the_same_deploys_at_the_range_edge() -> None:
    # NOW is 12:00 on Nov 17, so a 7-day range is Nov 11 00:00 to now. A
    # rolling `now - 7d` would have counted the Nov 10 15:00 deploy in the
    # tiles but in no daily bar.
    rows = [row(24 * 6 + 11), row(24 * 6 + 13), row(1)]  # Nov 11 01:00, Nov 10 23:00
    view = dp.build(dp.parse(rows), days=7, now=NOW)
    assert view.charts["dates"][0] == "2026-11-11"
    assert view.total == 2
    assert sum(sum(v) for v in view.charts["outcomes"].values()) == view.total
    assert view.charts["start"] == "2026-11-11T00:00:00.000Z"


def test_js_time_is_a_format_every_browser_parses() -> None:
    when = datetime(2026, 11, 17, 10, 0, 0, 123456, tzinfo=UTC)
    assert dp.js_time(when) == "2026-11-17T10:00:00.123Z"
    view = dp.build(dp.parse([row(1)]), days=7, now=NOW)
    assert view.charts["timeline"]["points"][0][0] == "2026-11-17T11:00:00.000Z"
