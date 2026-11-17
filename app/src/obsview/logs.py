"""Reading Log Analytics, and the cache in front of it.

Each view runs **one** query for raw rows and computes every tile and chart
from them in Python (see `deployments.py`). The tables are small (a deploy
is a row), so one round trip beats a query per panel, and the aggregation is
then plain code with plain tests rather than KQL that only the workspace can
check.

The query window is passed as `timespan`, never interpolated into the KQL.
The only value that is interpolated is the environment, which `Settings`
holds to a slug.
"""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import Callable
from datetime import timedelta
from typing import Any, Protocol

logger = logging.getLogger(__name__)

Row = dict[str, Any]


class QueryFailed(RuntimeError):
    """The workspace refused or failed a query. Shown on the page, not a 500."""


class LogsSource(Protocol):
    def query(self, kql: str, timespan: timedelta) -> list[Row]: ...


class AzureLogs:
    """Log Analytics through `azure-monitor-query`, as the app's identity."""

    def __init__(self, workspace_id: str, client_id: str = "") -> None:
        self._workspace_id = workspace_id
        self._client_id = client_id
        self._client: Any = None

    def _logs_client(self) -> Any:
        if self._client is None:
            # Imported lazily so tests and the demo never need a credential.
            from azure.monitor.query import LogsQueryClient

            if self._client_id:
                from azure.identity import ManagedIdentityCredential

                credential: Any = ManagedIdentityCredential(client_id=self._client_id)
            else:
                from azure.identity import DefaultAzureCredential

                credential = DefaultAzureCredential()
            self._client = LogsQueryClient(credential)
        return self._client

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        from azure.monitor.query import LogsQueryStatus

        try:
            result = self._logs_client().query_workspace(self._workspace_id, kql, timespan=timespan)
        except Exception as exc:
            logger.exception("Log Analytics query failed")
            raise QueryFailed(f"Log Analytics query failed: {type(exc).__name__}") from exc
        if result.status != LogsQueryStatus.SUCCESS:
            # A partial result means the workspace cut the query short, and a
            # dashboard built on part of the rows would quietly undercount.
            raise QueryFailed("Log Analytics returned a partial result")
        return [_row(table.columns, row) for table in result.tables for row in table.rows]


def _row(columns: Any, row: Any) -> Row:
    # Columns are plain names in azure-monitor-query >= 1.2; older releases
    # gave objects with a `.name`.
    names = [c if isinstance(c, str) else c.name for c in columns]
    return dict(zip(names, row, strict=True))


class CachedLogs:
    """A `LogsSource` that remembers each (query, timespan) for `ttl` seconds.

    One lock per key, so two requests for the same cold page wait on one
    query rather than both running it. A failure is not cached: the next
    request tries again.
    """

    def __init__(
        self, source: LogsSource, ttl: float, clock: Callable[[], float] = time.monotonic
    ) -> None:
        self._source = source
        self._ttl = ttl
        self._clock = clock
        self._entries: dict[tuple[str, timedelta], tuple[float, list[Row]]] = {}
        self._locks: dict[tuple[str, timedelta], threading.Lock] = {}
        self._guard = threading.Lock()

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        key = (kql, timespan)
        with self._guard:
            lock = self._locks.setdefault(key, threading.Lock())
        with lock:
            hit = self._entries.get(key)
            if hit and self._clock() - hit[0] < self._ttl:
                return hit[1]
            rows = self._source.query(kql, timespan)
            self._entries[key] = (self._clock(), rows)
            return rows
