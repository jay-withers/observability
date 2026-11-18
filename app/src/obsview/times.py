"""Timestamps from Log Analytics rows."""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any


def parse_time(value: Any) -> datetime | None:
    """An aware UTC datetime from a row's value, or None if it isn't one.

    The SDK returns datetimes; the demo and tests sometimes pass ISO strings.
    A malformed string is None, so one bad row is skipped rather than turning
    the whole page into a 500.
    """
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    if not isinstance(value, datetime):
        return None
    return value if value.tzinfo else value.replace(tzinfo=UTC)
