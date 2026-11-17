from __future__ import annotations

from datetime import timedelta
from types import SimpleNamespace

import pytest

from obsview.logs import CachedLogs, QueryFailed, Row, _row


class Counting:
    def __init__(self) -> None:
        self.calls = 0
        self.fail = False

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        self.calls += 1
        if self.fail:
            raise QueryFailed("boom")
        return [{"kql": kql, "n": self.calls}]


class Clock:
    def __init__(self) -> None:
        self.t = 0.0

    def __call__(self) -> float:
        return self.t


def test_cache_serves_repeat_queries_until_the_ttl_passes() -> None:
    source, clock = Counting(), Clock()
    cached = CachedLogs(source, ttl=300, clock=clock)
    span = timedelta(days=1)
    assert cached.query("q", span) == [{"kql": "q", "n": 1}]
    clock.t = 299
    assert cached.query("q", span)[0]["n"] == 1
    clock.t = 300
    assert cached.query("q", span)[0]["n"] == 2


def test_cache_keys_on_timespan_as_well_as_query() -> None:
    source = Counting()
    cached = CachedLogs(source, ttl=300, clock=Clock())
    cached.query("q", timedelta(days=1))
    cached.query("q", timedelta(days=2))
    assert source.calls == 2


def test_a_failure_is_not_cached() -> None:
    source = Counting()
    cached = CachedLogs(source, ttl=300, clock=Clock())
    source.fail = True
    with pytest.raises(QueryFailed):
        cached.query("q", timedelta(days=1))
    source.fail = False
    assert cached.query("q", timedelta(days=1))[0]["n"] == 2


def test_row_accepts_column_names_or_column_objects() -> None:
    assert _row(["A", "B"], [1, 2]) == {"A": 1, "B": 2}
    assert _row([SimpleNamespace(name="A")], [1]) == {"A": 1}
