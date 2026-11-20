from __future__ import annotations

from datetime import UTC, date, datetime, timedelta
from typing import Any

import pytest

from obsview import costs

NOW = datetime(2026, 11, 17, 12, 0, tzinfo=UTC)


def row(
    day: date,
    rg: str,
    service: str,
    cost: float,
    currency: str = "USD",
    resource: str = "",
) -> dict[str, Any]:
    return {
        "UsageDate": datetime(day.year, day.month, day.day, tzinfo=UTC),
        "ResourceGroup": rg,
        "ServiceName": service,
        "ResourceId": f"/subscriptions/s/resourceGroups/{rg}/providers/p/{resource}"
        if resource
        else "",
        "Cost": cost,
        "Currency": currency,
    }


def build(rows: list[dict[str, Any]], days: int = 30) -> costs.View:
    return costs.build(costs.parse(rows), days, NOW, budget=150.0)


def test_month_to_date_counts_this_month_only() -> None:
    view = build(
        [
            row(date(2026, 10, 31), "rg-platform-dev", "Log Analytics", 99.0),
            row(date(2026, 11, 1), "rg-platform-dev", "Log Analytics", 2.0),
            row(date(2026, 11, 16), "rg-gymlog-dev", "Storage", 1.0),
        ]
    )
    assert view.month_to_date == pytest.approx(3.0)
    assert view.budget_used == pytest.approx(2.0)
    assert view.month_label == "November"


def test_forecast_divides_by_days_with_data_not_days_elapsed() -> None:
    # Data through the 10th (the feed lags), $1 a day: $10 over 10 days of
    # data, so $30 for November's 30 days. Dividing by the 17th would say $17.6.
    rows = [row(date(2026, 11, d), "rg-platform-dev", "x", 1.0) for d in range(1, 11)]
    view = build(rows)
    assert view.data_through == date(2026, 11, 10)
    assert view.forecast == pytest.approx(30.0)


def test_no_data_this_month_means_no_forecast() -> None:
    view = build([row(date(2026, 10, 30), "rg-platform-dev", "x", 5.0)])
    assert view.month_to_date == 0
    assert view.forecast is None


def test_areas_are_fixed_by_resource_group_case_insensitively() -> None:
    assert costs.area("rg-platform-dev") == "platform"
    assert costs.area("RG-GYMLOG-DEV") == "apps"
    assert costs.area("rg-something-new") == costs.OTHER


def test_daily_series_stack_by_area_over_the_range() -> None:
    rows = [
        row(date(2026, 11, 17), "rg-platform-dev", "a", 1.0),
        row(date(2026, 11, 17), "rg-gymlog-dev", "b", 0.5),
        row(date(2026, 11, 17), "rg-hub-dev", "c", 0.25),
        row(date(2026, 11, 1), "rg-platform-dev", "a", 9.0),  # outside a 7-day range
    ]
    spend = build(rows, days=7).charts["spend"]
    assert len(spend["dates"]) == 7
    assert spend["series"]["platform"][-1] == 1.0
    assert spend["series"]["apps"][-1] == 0.5
    assert spend["series"][costs.OTHER][-1] == 0.25
    assert sum(spend["series"]["platform"]) == 1.0


def test_bars_fold_the_tail_into_everything_else() -> None:
    rows = [row(date(2026, 11, 10), "rg-platform-dev", f"svc{i}", float(10 - i)) for i in range(10)]
    services = build(rows).services
    assert len(services) == 8
    assert services[-1].label == "everything else"
    assert services[-1].value == pytest.approx(3 + 2 + 1)
    assert services[0].width == 100.0


def test_resource_group_bars_carry_their_areas_colour() -> None:
    view = build([row(date(2026, 11, 10), "rg-aks-dev", "AKS", 4.0)])
    aks = costs.AREA_ORDER.index("aks") + 1
    assert [(b.label, b.colour) for b in view.groups] == [("rg-aks-dev", aks)]
    assert [(b.label, b.colour) for b in view.areas] == [("aks", aks)]


def slice_view(rows: list[dict[str, Any]], days: int = 30) -> costs.View:
    return costs.build(costs.parse(rows), days, NOW, budget=150.0, by_area=False)


def test_a_slice_stacks_its_own_services_and_names_no_other_area() -> None:
    rows = [
        row(date(2026, 11, 17), "rg-gymlog-dev", "Container Apps", 1.0),
        row(date(2026, 11, 17), "rg-gymlog-dev", "Storage", 0.25),
    ]
    view = slice_view(rows, days=7)
    assert view.charts["series_keys"] == ["Container Apps", "Storage"]
    assert view.charts["spend"]["series"]["Container Apps"][-1] == 1.0
    assert set(view.charts["spend"]["series"]) == {"Container Apps", "Storage"}
    # The treemap starts at the slice's resource groups, not the estate's areas.
    assert view.charts["treemap"] == [
        {
            "name": "rg-gymlog-dev",
            "children": [
                {
                    "name": costs.UNATTRIBUTED,
                    "children": [
                        {"name": "Container Apps", "value": 1.0},
                        {"name": "Storage", "value": 0.25},
                    ],
                }
            ],
        }
    ]
    assert all(b.colour == 0 for b in view.groups)


def test_a_slices_daily_series_fold_the_tail_and_still_sum_to_the_total() -> None:
    rows = [row(date(2026, 11, 17), "rg-platform-dev", f"svc{i}", float(10 - i)) for i in range(8)]
    view = slice_view(rows, days=1)
    keys = view.charts["series_keys"]
    assert len(keys) == costs.SERVICE_SERIES
    assert keys[-1] == costs.REST
    series = view.charts["spend"]["series"]
    assert sum(v[-1] for v in series.values()) == pytest.approx(view.total)
    assert series[costs.REST][-1] == pytest.approx(6 + 5 + 4 + 3)


def test_treemap_is_area_then_group_then_service_in_area_order() -> None:
    rows = [
        row(date(2026, 11, 10), "rg-gymlog-dev", "Storage", 1.0),
        row(date(2026, 11, 10), "rg-platform-dev", "Log Analytics", 2.0),
    ]
    tree = build(rows).charts["treemap"]
    assert [n["name"] for n in tree] == ["platform", "apps"]
    assert tree[1]["children"][0] == {
        "name": "rg-gymlog-dev",
        "children": [{"name": costs.UNATTRIBUTED, "children": [{"name": "Storage", "value": 1.0}]}],
    }


def test_currency_symbol() -> None:
    assert build([row(date(2026, 11, 10), "rg", "s", 1.0, "GBP")]).symbol == "£"
    assert build([row(date(2026, 11, 10), "rg", "s", 1.0, "CHF")]).symbol == "CHF "


def test_gauge_scale_fits_the_largest_of_budget_forecast_and_spend() -> None:
    rows = [row(date(2026, 11, d), "rg-platform-dev", "x", 10.0) for d in range(1, 11)]
    view = build(rows)  # $100 so far, $300 forecast, $150 budget
    assert view.scale(view.forecast or 0) == pytest.approx(100 / 1.05)
    assert view.scale(view.budget) < view.scale(view.forecast or 0)


def test_window_covers_the_month_or_two_ranges_and_the_repost_week() -> None:
    assert costs.window(1) == timedelta(days=39)
    assert costs.window(7) == timedelta(days=39)
    assert costs.window(90) == timedelta(days=188)


def test_daily_series_end_at_the_last_day_with_data() -> None:
    # The feed lags: nothing yet for the 16th or 17th, so the chart ends on
    # the 15th rather than dropping to zero for two days.
    spend = build([row(date(2026, 11, 15), "rg-platform-dev", "a", 1.0)], days=7).charts["spend"]
    assert spend["dates"][-1] == "2026-11-15"
    assert spend["series"]["platform"][-1] == 1.0


def test_only_the_billing_currency_is_summed() -> None:
    rows = [
        row(date(2026, 11, 10), "rg-platform-dev", "a", 10.0, "USD"),
        row(date(2026, 11, 11), "rg-platform-dev", "a", 10.0, "USD"),
        row(date(2026, 11, 11), "rg-platform-dev", "b", 99.0, "GBP"),
    ]
    view = build(rows)
    assert view.month_to_date == pytest.approx(20.0)
    assert view.symbol == "$"
    assert view.left_out == 1


def test_early_in_the_month_data_through_shows_last_month() -> None:
    # 1 Nov, with the feed through 31 Oct: nothing this month yet, but the
    # page still says how fresh the data is.
    view = costs.build(
        costs.parse([row(date(2026, 10, 31), "rg-platform-dev", "a", 1.0)]),
        30,
        datetime(2026, 11, 1, 9, 0, tzinfo=UTC),
        budget=150.0,
    )
    assert view.month_to_date == 0
    assert view.forecast is None
    assert view.data_through == date(2026, 10, 31)


def test_a_malformed_date_skips_the_row() -> None:
    rows = [{"UsageDate": "not a date", "ResourceGroup": "rg", "ServiceName": "s", "Cost": 1.0}]
    assert costs.parse(rows) == []


def test_breakdowns_follow_the_range_not_the_month() -> None:
    rows = [
        row(date(2026, 11, 15), "rg-platform-dev", "Log Analytics", 1.0),
        row(date(2026, 11, 10), "rg-gymlog-dev", "Storage", 5.0),  # outside 3 days
        row(date(2026, 10, 30), "rg-aks-dev", "AKS", 7.0),  # last month, inside 30 days
    ]
    short = build(rows, days=3)
    assert short.total == pytest.approx(1.0)
    assert [b.label for b in short.groups] == ["rg-platform-dev"]
    assert [n["name"] for n in short.charts["treemap"]] == ["platform"]
    wide = build(rows, days=30)
    assert wide.total == pytest.approx(13.0)
    assert {b.label for b in wide.services} == {"Log Analytics", "Storage", "AKS"}
    assert wide.month_to_date == pytest.approx(6.0)  # the budget card stays the month


def test_range_ends_on_the_last_day_with_data() -> None:
    # The feed is through the 15th; a one-day range is the 15th, not an
    # empty 17th.
    view = build([row(date(2026, 11, 15), "rg-platform-dev", "a", 2.0)], days=1)
    assert (view.range_start, view.range_end) == (date(2026, 11, 15), date(2026, 11, 15))
    assert view.total == pytest.approx(2.0)


def test_an_explicit_end_is_kept_for_a_quiet_app() -> None:
    view = costs.build(
        costs.parse([row(date(2026, 11, 10), "rg-gymlog-dev", "Storage", 1.0)]),
        3,
        NOW,
        budget=150.0,
        end=date(2026, 11, 16),
    )
    assert view.range_end == date(2026, 11, 16)
    assert view.total == 0


def test_delta_against_the_prior_range() -> None:
    rows = [
        row(date(2026, 11, 15), "rg-platform-dev", "a", 3.0),
        row(date(2026, 11, 14), "rg-platform-dev", "a", 1.0),  # prior day
    ]
    view = build(rows, days=1)
    assert view.prior_total == pytest.approx(1.0)
    assert view.delta == "+$2.00 vs prior 1d"
    assert view.tone == "bad"


def test_no_delta_when_the_data_does_not_reach_the_prior_range() -> None:
    # Retention: nothing before the 14th, so a 3-day range's prior (9th-11th)
    # would read as a rise from nothing.
    rows = [row(date(2026, 11, d), "rg-platform-dev", "a", 1.0) for d in (14, 15)]
    view = build(rows, days=3)
    assert view.prior_total is None
    assert view.delta is None


def test_spend_matches_builds_total() -> None:
    rows = costs.parse(
        [
            row(date(2026, 11, 15), "rg-gymlog-dev", "a", 2.0),
            row(date(2026, 11, 10), "rg-gymlog-dev", "a", 3.0),
            row(date(2026, 11, 15), "rg-gymlog-dev", "a", 9.0, "GBP"),
        ]
    )
    end = date(2026, 11, 15)
    assert costs.spend(rows, end, 3) == costs.build(rows, 3, NOW, 150.0, end=end).total == 2.0


def test_a_slices_chart_keeps_a_service_that_nets_to_a_credit() -> None:
    rows = [
        row(date(2026, 11, 16), "rg-gymlog-dev", "Compute", 5.0),
        row(date(2026, 11, 16), "rg-gymlog-dev", "Storage", 2.0),
        row(date(2026, 11, 17), "rg-gymlog-dev", "Storage", -3.0),
    ]
    view = slice_view(rows, days=2)
    series = view.charts["spend"]["series"]
    assert "Storage" in view.charts["series_keys"]
    assert sum(sum(v) for v in series.values()) == pytest.approx(view.total)


def test_resources_are_keyed_by_id_and_labelled_with_their_group() -> None:
    rows = [
        row(date(2026, 11, 10), "rg-gymlog-dev", "Storage", 2.0, resource="default"),
        row(date(2026, 11, 10), "rg-career-dev", "Storage", 1.0, resource="default"),
        row(date(2026, 11, 10), "rg-career-dev", "Key Vault", 0.5),
    ]
    view = build(rows)
    assert [(b.label, b.value) for b in view.resources] == [
        ("default · rg-gymlog-dev", 2.0),
        ("default · rg-career-dev", 1.0),
        (f"{costs.UNATTRIBUTED} · rg-career-dev", 0.5),
    ]


def test_the_treemap_has_a_resource_level() -> None:
    rows = [row(date(2026, 11, 10), "rg-gymlog-dev", "Storage", 1.0, resource="STGymlog")]
    tree = slice_view(rows).charts["treemap"]
    assert tree == [
        {
            "name": "rg-gymlog-dev",
            "children": [{"name": "stgymlog", "children": [{"name": "Storage", "value": 1.0}]}],
        }
    ]


def test_same_named_resources_of_different_types_stay_apart_in_the_treemap() -> None:
    rows = [
        row(date(2026, 11, 10), "rg-x", "Container Apps", 2.0, resource="containerApps/ca-x"),
        row(date(2026, 11, 10), "rg-x", "Managed Identity", 1.0, resource="identities/ca-x"),
    ]
    group = slice_view(rows).charts["treemap"][0]
    assert [(n["name"], n["children"][0]["value"]) for n in group["children"]] == [
        ("ca-x", 2.0),
        ("ca-x", 1.0),
    ]
