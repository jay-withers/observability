from __future__ import annotations

from datetime import UTC, datetime, timedelta
from typing import Any

from obsview import errors

NOW = datetime(2026, 11, 17, 12, 0, tzinfo=UTC)


def test_numbers_ids_and_quoted_values_are_masked() -> None:
    a = errors.signature("ERROR sqlalchemy.pool: connection 41 was closed for 'alice'")
    b = errors.signature("ERROR sqlalchemy.pool: connection 7 was closed for 'bob'")
    assert a == b
    assert errors.signature("ERROR job deadbeefcafe0001 failed") == errors.signature(
        "ERROR job 0123456789abcdef failed"
    )


def test_a_leading_timestamp_is_ignored() -> None:
    assert errors.signature("2026-11-17T10:00:01.123Z ERROR boom") == errors.signature(
        "[2026-11-16 09:00:00,456] ERROR boom"
    )


def test_access_lines_group_by_method_path_and_status() -> None:
    line = 'INFO:     10.0.0.4:5123 - "GET /sessions/12?x=1 HTTP/1.1" 500 Internal Server Error'
    assert errors.signature(line) == "GET /sessions/N → 500"


def test_only_the_first_line_counts() -> None:
    assert errors.signature("ERROR boom\n  File 'a.py', line 3") == "ERROR boom"


def test_groups_count_and_keep_the_latest_sample_newest_first() -> None:
    rows: list[dict[str, Any]] = [
        {"TimeGenerated": NOW - timedelta(hours=3), "Workload": "ca-a", "Log_s": "ERROR x 1"},
        {"TimeGenerated": NOW - timedelta(hours=1), "Workload": "ca-a", "Log_s": "ERROR x 2"},
        {"TimeGenerated": NOW - timedelta(hours=2), "Workload": "ca-b", "Log_s": "ERROR x 3"},
        {"TimeGenerated": NOW, "Workload": "ca-a", "Log_s": "CRITICAL y"},
        {"TimeGenerated": "not a time", "Workload": "ca-a", "Log_s": "ERROR z"},
    ]
    groups = errors.group(errors.parse(rows))
    assert [(g.workload, g.signature, g.count) for g in groups] == [
        ("ca-a", "CRITICAL y", 1),
        ("ca-a", "ERROR x N", 2),
        ("ca-b", "ERROR x N", 1),
    ]
    assert groups[1].sample == "ERROR x 2"
    assert groups[1].first == NOW - timedelta(hours=3)


def test_query_reads_fuzzily_and_caps_each_workload() -> None:
    assert "union isfuzzy=true ContainerAppConsoleLogs_CL" in errors.KQL
    assert f"where Rank <= {errors.PER_WORKLOAD}" in errors.KQL


def test_uuids_are_masked_whole() -> None:
    a = errors.signature("ERROR job 3f2a9c1e-ab4d-4ef0-9abc-0123456789ab failed")
    b = errors.signature("ERROR job 00000000-cd5e-11ee-8abc-ffffffffffff failed")
    assert a == b == "ERROR job … failed"


def test_ids_in_access_paths_are_masked() -> None:
    a = errors.signature('"GET /runs/ab12cd34ef/x HTTP/1.1" 500')
    b = errors.signature('"GET /runs/9f8e7d6c5b/x HTTP/1.1" 500')
    assert a == b == "GET /runs/…/x → 500"


def test_totals_come_from_the_query_past_the_cap() -> None:
    rows: list[dict[str, Any]] = [
        {"Workload": "ca-a", "Total": 5000},
        {"Workload": "ca-a", "Total": 5000},
        {"Workload": "ca-b"},
        {"Workload": "ca-b"},
    ]
    assert errors.totals(rows) == {"ca-a": 5000, "ca-b": 2}


def test_window_reaches_the_ranges_first_midnight_in_five_minute_steps() -> None:
    since = datetime(2026, 11, 17, tzinfo=UTC)
    assert errors.window(since, since + timedelta(hours=1, seconds=1)) == timedelta(
        hours=1, minutes=5
    )
    assert errors.window(since, since + timedelta(hours=1)) == timedelta(hours=1)
