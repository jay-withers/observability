"""The Jobs and ops view, from `OpsEvents_CL` and failed job runs.

`OpsEvents_CL` holds one row per check per run, written by this repo's
scheduled checks and by workflows' drift check: `heartbeat` (one per
Container Apps job, plus `check: <name>` for each daily check), `drift` (one
per Terraform root), `expiry` (one per custom domain), `session` (one per
category of billed-by-the-hour resource) and `cost-feed`.

"Now" is each check's latest row within the last two days, as Grafana's
panels read it: a check that stopped writing drops out here, and its
`check:` heartbeat goes overdue instead.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, timedelta
from typing import Any

from .logs import Row
from .times import parse_time

# How bad a status is, for colour and for "worst of the day". Anything not
# listed (a status a future check invents) reads as neutral rather than ok.
SEVERITY = {
    "ok": 0,
    "removed": 1,
    "pending": 1,
    "overdue": 2,
    "drifted": 2,
    "expiring": 2,
    "running": 2,
    "failed": 3,
}
NEUTRAL = 1
# The statuses that count as a problem now, as Grafana's "Problems now" did.
PROBLEMS = frozenset({"failed", "drifted", "expiring", "overdue", "running"})
FRESH = timedelta(days=2)
# The status grid shows at most this many days: at 90 a cell is too thin to
# tap on a phone, and a check's history past a month is rarely the question.
GRID_DAYS = 30

SECTIONS = (
    ("heartbeat", "Jobs and checks", "Each job's last success against its usual cadence"),
    ("drift", "Terraform drift", "Nightly plan of every root"),
    ("expiry", "Certificates", "TLS expiry per custom domain"),
    ("session", "Session resources", "Billed-by-the-hour resources left running"),
    ("cost-feed", "Cost feed", "The daily Costs_CL import"),
)

# Sections drawn as gauges rather than a status grid.
GAUGED = frozenset({"expiry"})

DAYS_LEFT = re.compile(r"\((\d+) days?\)")


def kql(environment: str) -> str:
    return (
        "OpsEvents_CL\n"
        f'| where Environment == "{environment}"\n'
        "| project TimeGenerated, Kind, Name, Status, Detail, RunUrl, Repo"
    )


# Container Apps' own record of job executions that failed, and of apps
# whose container crashed (the same reasons azure-container-apps' crash alert
# reads). Summarised in KQL rather than fetched raw: a crash-looping job or
# revision writes a row per attempt.
#
# The table exists only once something has logged to it, so it is read
# through a fuzzy union with an empty datatable (as azure-container-apps'
# alerts do): a workspace where nothing has run yet answers with no rows
# rather than "Failed to resolve table". A row names a job or an app, never
# both, so `coalesce` gives the one workload name.
FAILED_RUNS_KQL = """union isfuzzy=true ContainerAppSystemLogs_CL,
    (datatable(TimeGenerated: datetime, JobName_s: string, ContainerAppName_s: string,
               Reason_s: string, Log_s: string)[])
| extend Workload = coalesce(JobName_s, ContainerAppName_s)
| where isnotempty(Workload)
| where Reason_s in ("BackoffLimitExceeded", "DeadlineExceeded", "ContainerCrashing", "StartError")
| summarize Failures = count(), LastFailure = max(TimeGenerated), Sample = take_any(Log_s)
    by Job = Workload, Reason = Reason_s
| order by LastFailure desc"""


def window(days: int) -> timedelta:
    return timedelta(days=max(min(days, GRID_DAYS), 2))


@dataclass(frozen=True)
class Event:
    when: datetime
    kind: str
    name: str
    status: str
    detail: str
    run_url: str

    @property
    def severity(self) -> int:
        return SEVERITY.get(self.status, NEUTRAL)

    @property
    def problem(self) -> bool:
        return self.status in PROBLEMS


@dataclass(frozen=True)
class FailedRun:
    job: str
    reason: str
    failures: int
    last: datetime
    sample: str


@dataclass
class Section:
    kind: str
    title: str
    sub: str
    current: list[Event]
    problems: int

    @property
    def worst(self) -> str:
        """The status of the section's worst problem, which colours its badge."""
        problems = [e for e in self.current if e.problem]
        return max(problems, key=lambda e: e.severity).status if problems else "ok"


@dataclass
class Certificate:
    name: str
    days_left: int | None
    status: str
    detail: str
    # 0-100, for the bar: a year of validity fills it.
    width: float


@dataclass
class View:
    days: int
    now: datetime
    problems: list[Event]
    tiles: list[tuple[str, int, str]]
    sections: list[Section]
    certificates: list[Certificate]
    failed_runs: list[FailedRun] | None
    charts: dict[str, Any] = field(default_factory=dict)


def parse(rows: list[Row]) -> list[Event]:
    events = []
    for row in rows:
        when = parse_time(row.get("TimeGenerated"))
        if when is None:
            continue
        events.append(
            Event(
                when=when,
                kind=str(row.get("Kind") or ""),
                name=str(row.get("Name") or ""),
                status=str(row.get("Status") or "unknown"),
                detail=str(row.get("Detail") or ""),
                run_url=str(row.get("RunUrl") or ""),
            )
        )
    return events


def parse_failed_runs(rows: list[Row]) -> list[FailedRun]:
    runs = []
    for row in rows:
        last = parse_time(row.get("LastFailure"))
        if last is None:
            continue
        runs.append(
            FailedRun(
                job=str(row.get("Job") or ""),
                reason=str(row.get("Reason") or ""),
                failures=int(row.get("Failures") or 0),
                last=last,
                sample=str(row.get("Sample") or "")[:300],
            )
        )
    runs.sort(key=lambda r: r.last, reverse=True)
    return runs


def latest(events: list[Event], now: datetime) -> dict[tuple[str, str], Event]:
    """Each (kind, name)'s newest row, among those from the last two days."""
    current: dict[tuple[str, str], Event] = {}
    for e in events:
        if now - e.when > FRESH:
            continue
        key = (e.kind, e.name)
        if key not in current or e.when > current[key].when:
            current[key] = e
    return current


def build(
    events: list[Event], failed_runs: list[FailedRun] | None, days: int, now: datetime
) -> View:
    """`failed_runs` None means that query failed: the card says so, the rest renders."""
    current = latest(events, now)

    def order(e: Event) -> tuple[int, str]:
        # Worst first; `removed` sinks below `ok`, as in Grafana's tables.
        return (-e.severity if e.status != "removed" else 1, e.name)

    sections = []
    for kind, title, sub in SECTIONS:
        rows = sorted((e for (k, _), e in current.items() if k == kind), key=order)
        if rows:
            sections.append(Section(kind, title, sub, rows, sum(e.problem for e in rows)))

    problems = sorted((e for e in current.values() if e.problem), key=order)

    def count(kind: str, statuses: frozenset[str]) -> int:
        return sum(1 for (k, _), e in current.items() if k == kind and e.status in statuses)

    tiles = [
        ("Jobs overdue", count("heartbeat", frozenset({"overdue"})), "heartbeat"),
        ("Roots drifted", count("drift", frozenset({"drifted", "failed"})), "drift"),
        ("Certificates expiring", count("expiry", frozenset({"expiring", "failed"})), "expiry"),
        ("Sessions running", count("session", frozenset({"running"})), "session"),
    ]

    grid_days = min(days, GRID_DAYS)
    dates = [now.astimezone(UTC).date() - timedelta(days=i) for i in range(grid_days - 1, -1, -1)]
    charts = {
        "dates": [d.isoformat() for d in dates],
        "grids": {s.kind: _grid(events, s, dates) for s in sections if s.kind not in GAUGED},
    }
    return View(
        days=days,
        now=now,
        problems=problems,
        tiles=tiles,
        sections=sections,
        certificates=_certificates(current),
        failed_runs=failed_runs,
        charts=charts,
    )


def _grid(events: list[Event], section: Section, dates: list[date]) -> dict[str, Any]:
    """Worst status per name per day: [day index, row index, severity, status]."""
    names = [e.name for e in section.current]
    index = {n: i for i, n in enumerate(names)}
    day_index = {d: i for i, d in enumerate(dates)}
    worst: dict[tuple[int, int], Event] = {}
    for e in events:
        if e.kind != section.kind or e.name not in index:
            continue
        d = day_index.get(e.when.astimezone(UTC).date())
        if d is None:
            continue
        key = (d, index[e.name])
        if key not in worst or e.severity > worst[key].severity:
            worst[key] = e
    # Names reversed for ECharts, which draws the first category at the
    # bottom, and each cell's row index flipped to match.
    last = len(names) - 1
    cells = [[d, last - r, e.severity, e.status] for (d, r), e in sorted(worst.items())]
    return {"names": names[::-1], "cells": cells}


def _certificates(current: dict[tuple[str, str], Event]) -> list[Certificate]:
    certs = []
    for (kind, name), e in current.items():
        if kind != "expiry":
            continue
        match = DAYS_LEFT.search(e.detail)
        days_left = int(match.group(1)) if match else None
        width = 0.0 if days_left is None else max(2.0, min(100.0, 100.0 * days_left / 365))
        certs.append(Certificate(name, days_left, e.status, e.detail, width))
    certs.sort(key=lambda c: (c.days_left is None, c.days_left or 0, c.name))
    return certs
