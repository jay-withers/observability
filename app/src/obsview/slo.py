"""Service level objectives: each app's SLIs against a target, over 28 days.

An SLI here is always good events over valid events, so every objective has
the same arithmetic and the same error budget:

- **Availability** (apps that serve HTTP): requests the ingress answered
  without a 5xx, from `ContainerAppHTTPLogs`. A cold start's Envoy 503 counts
  against it: the caller saw it.
- **Latency** (apps that serve HTTP): requests answered within
  `LATENCY_MS`, of those with a duration logged.
- **Job runs**: Container Apps job executions that completed, of those that
  finished (completed, or failed past their retries or deadline), from
  `ContainerAppSystemLogs_CL`. One still running is not counted yet.
- **Deploys**: deploys that succeeded (`deployments.SUCCEEDED`), from
  `Deployments_CL`.

The window is a rolling `WINDOW_DAYS` whole UTC days, today the last,
whatever the page's range: an SLO is defined over its window, and 28 days
(four whole weeks) stays inside the workspace's 30-day retention for the
Container Apps logs.

The error budget is the bad events the target allows over the window,
`(1 - target) * valid`; what is left of it is `1 - bad / allowed`, negative
once spent. The status reads only that: `ok` with a quarter or more left,
`at-risk` under a quarter, `exhausted` at none. Burn rates (how fast the last
day and week spent it, 1 being exactly on budget) are shown, not alerted on:
with a few hundred requests a day one 5xx moves a day's rate a lot, and
alerting stays in Azure Monitor.

The two queries count in KQL (the HTTP logs are a row per request, too many
to fetch raw) by hour and workload, and by job execution; every ratio and
budget is computed here.
"""

from __future__ import annotations

import math
from collections.abc import Iterable
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, time, timedelta
from typing import Any

from .deployments import Deploy
from .logs import Row
from .times import parse_time

WINDOW_DAYS = 28
WINDOW = timedelta(days=WINDOW_DAYS)
# A request answered within this many milliseconds is fast. Written into the
# KQL below as a literal; a test keeps the two in step.
LATENCY_MS = 1000
# Less than this fraction of the budget left is "at risk".
AT_RISK = 0.25

HTTP_KQL = """union isfuzzy=true ContainerAppHTTPLogs,
    (datatable(TimeGenerated: datetime, ContainerAppName: string)[])
| where isnotempty(ContainerAppName)
| extend Status = toint(column_ifexists("StatusCode", int(null))),
    Ms = todouble(column_ifexists("DurationMs", real(null)))
| summarize Total = count(), Errors = countif(Status >= 500),
    Timed = countif(isnotnull(Ms)), Fast = countif(Ms <= 1000)
    by Hour = bin(TimeGenerated, 1h), Workload = ContainerAppName"""

# One row per job execution: when it started, and whether it completed or
# failed. `BackoffLimitExceeded` and `DeadlineExceeded` are the reasons the
# failed-runs query and azure-container-apps' alert read as a failed run.
# `Started` is the create event's time, null for a run created before the
# window, which `parse_jobs` leaves out: a run counts in the window it began.
JOBS_KQL = """union isfuzzy=true ContainerAppSystemLogs_CL,
    (datatable(TimeGenerated: datetime, JobName_s: string, ExecutionName_s: string,
               Reason_s: string)[])
| where isnotempty(JobName_s) and isnotempty(ExecutionName_s)
| where Reason_s in ("SuccessfulCreate", "Completed", "BackoffLimitExceeded", "DeadlineExceeded")
| summarize Started = minif(TimeGenerated, Reason_s == "SuccessfulCreate"),
    Completed = countif(Reason_s == "Completed"),
    Failed = countif(Reason_s in ("BackoffLimitExceeded", "DeadlineExceeded"))
    by Job = JobName_s, Execution = ExecutionName_s"""


@dataclass(frozen=True)
class Objective:
    key: str
    title: str
    target: float
    good: str
    unit: str
    web_only: bool = False


OBJECTIVES: tuple[Objective, ...] = (
    Objective(
        "availability",
        "Availability",
        0.995,
        "Requests answered without a 5xx",
        "requests",
        web_only=True,
    ),
    Objective(
        "latency",
        "Latency",
        0.95,
        f"Requests answered within {LATENCY_MS:,} ms",
        "requests",
        web_only=True,
    ),
    Objective("jobs", "Job runs", 0.95, "Job executions that completed", "runs"),
    Objective("deploys", "Deploys", 0.90, "Deploys that succeeded", "deploys"),
)

# Worst first, for an app's overall status and the badge colours.
STATUSES = ("exhausted", "at-risk", "unavailable", "ok", "no-data")
LABELS = {
    "exhausted": "budget spent",
    "at-risk": "at risk",
    "unavailable": "unavailable",
    "ok": "within budget",
    "no-data": "no data",
}


@dataclass(frozen=True)
class Point:
    """Some events at a time: an hour of requests, a job run, a deploy."""

    when: datetime
    good: int
    total: int


@dataclass(frozen=True)
class HttpHour:
    when: datetime
    workload: str
    total: int
    errors: int
    timed: int
    fast: int


@dataclass(frozen=True)
class JobRun:
    when: datetime
    job: str
    ok: bool


def _int(value: Any) -> int:
    return int(value) if isinstance(value, int | float) else 0


def parse_http(rows: list[Row]) -> list[HttpHour]:
    out = []
    for row in rows:
        when = parse_time(row.get("Hour"))
        if when is None:
            continue
        out.append(
            HttpHour(
                when=when,
                workload=str(row.get("Workload") or ""),
                total=_int(row.get("Total")),
                errors=_int(row.get("Errors")),
                timed=_int(row.get("Timed")),
                fast=_int(row.get("Fast")),
            )
        )
    return out


def parse_jobs(rows: list[Row]) -> list[JobRun]:
    """Finished runs only: a run with neither outcome yet is still going."""
    out = []
    for row in rows:
        when = parse_time(row.get("Started"))
        failed, completed = _int(row.get("Failed")), _int(row.get("Completed"))
        if when is None or not (failed or completed):
            continue
        # Failed wins: a run that completed on a retry after a crash still
        # completed, but one past its backoff limit did not.
        out.append(JobRun(when=when, job=str(row.get("Job") or ""), ok=not failed))
    return out


def availability_points(hours: Iterable[HttpHour]) -> list[Point]:
    return [Point(h.when, h.total - h.errors, h.total) for h in hours]


def latency_points(hours: Iterable[HttpHour]) -> list[Point]:
    return [Point(h.when, h.fast, h.timed) for h in hours]


def job_points(runs: Iterable[JobRun]) -> list[Point]:
    return [Point(r.when, int(r.ok), 1) for r in runs]


def deploy_points(deploys: Iterable[Deploy]) -> list[Point]:
    return [Point(d.when, int(d.succeeded), 1) for d in deploys]


@dataclass
class Sli:
    objective: Objective
    status: str
    good: int = 0
    total: int = 0
    ratio: float | None = None
    allowed: float = 0.0
    budget_left: float | None = None
    burn_day: float | None = None
    burn_week: float | None = None
    # [day, budget left %, bad that day, valid that day], oldest first.
    burndown: list[list[Any]] = field(default_factory=list)

    @property
    def bad(self) -> int:
        return self.total - self.good

    @property
    def label(self) -> str:
        return LABELS[self.status]


def _burn(points: list[Point], since: datetime, target: float) -> float | None:
    """How fast the points since `since` spent the budget: 1 is on budget."""
    recent = [p for p in points if p.when >= since]
    total = sum(p.total for p in recent)
    if not total:
        return None
    return (total - sum(p.good for p in recent)) / total / (1 - target)


def status_of(budget_left: float | None) -> str:
    if budget_left is None:
        return "no-data"
    if budget_left <= 0:
        return "exhausted"
    if budget_left < AT_RISK:
        return "at-risk"
    return "ok"


def window_start(now: datetime) -> datetime:
    """The window is whole UTC days, today the last: its first midnight. So
    the burn-down's days hold exactly the events the totals count, and an
    hourly bin is never cut in two at the start."""
    first = now.astimezone(UTC).date() - timedelta(days=WINDOW_DAYS - 1)
    return datetime.combine(first, time.min, tzinfo=UTC)


def _hour(when: datetime) -> datetime:
    return when.replace(minute=0, second=0, microsecond=0)


def measure(objective: Objective, points: list[Point] | None, now: datetime) -> Sli:
    """The SLI over the window ending `now`. None means its query failed."""
    if points is None:
        return Sli(objective, "unavailable")
    since = window_start(now)
    inside = [p for p in points if since <= p.when <= now]
    total = sum(p.total for p in inside)
    good = sum(p.good for p in inside)
    allowed = (1 - objective.target) * total
    # Rounded, so a budget spent exactly reads 0 rather than a hair either side.
    budget_left = None if not total else round(1 - (total - good) / allowed, 9)

    # A day per row, oldest first, the last being today (so far).
    today = now.astimezone(UTC).date()
    days = [today - timedelta(days=n) for n in range(WINDOW_DAYS - 1, -1, -1)]
    per_day: dict[date, list[int]] = {d: [0, 0] for d in days}
    for p in inside:
        slot = per_day.get(p.when.astimezone(UTC).date())
        if slot is not None:
            slot[0] += p.total - p.good
            slot[1] += p.total
    burndown: list[list[Any]] = []
    spent = 0
    for d in days:
        bad, valid = per_day[d]
        spent += bad
        left = None if not allowed else round(100 * (1 - spent / allowed), 2)
        burndown.append([d.isoformat(), left, bad, valid])

    return Sli(
        objective=objective,
        status=status_of(budget_left),
        good=good,
        total=total,
        ratio=None if not total else good / total,
        allowed=allowed,
        budget_left=budget_left,
        # From the hour, so the HTTP rows' hourly bin at the start counts whole.
        burn_day=_burn(inside, _hour(now - timedelta(days=1)), objective.target),
        burn_week=_burn(inside, _hour(now - timedelta(days=7)), objective.target),
        burndown=burndown,
    )


def fmt_ratio(ratio: float | None) -> str:
    """A ratio as a percentage with the decimals a nines target needs (two at
    99% and up), truncated rather than rounded, so a miss never reads as
    100%."""
    if ratio is None:
        return "–"
    digits = 2 if ratio >= 0.99 else 1
    scale = 10**digits
    value = math.floor(100 * ratio * scale + 1e-9) / scale
    return f"{value:.{digits}f}".rstrip("0").rstrip(".") + "%"


def fmt_budget(left: float) -> str:
    """How much of a budget is left, or overspent (the caller says which),
    truncated towards zero so it never reads as more than it is: one decimal
    under 1%, and never 0% unless it is exactly 0."""
    value = abs(100 * left)
    if value == 0 or value >= 1:
        return f"{math.floor(value + 1e-9)}%"
    return "<0.1%" if value < 0.1 else f"{math.floor(value * 10 + 1e-9) / 10:.1f}%"


def worst(statuses: Iterable[str]) -> str:
    present = set(statuses)
    return next((s for s in STATUSES if s in present), "no-data")


@dataclass
class Report:
    """One app's SLOs: its tile on /slo, and its page."""

    slug: str
    title: str
    slis: list[Sli]

    @property
    def status(self) -> str:
        return worst(s.status for s in self.slis)

    @property
    def label(self) -> str:
        return LABELS[self.status]

    @property
    def measured(self) -> list[Sli]:
        """The SLIs with events in the window, or whose query failed."""
        return [s for s in self.slis if s.status != "no-data"]

    @property
    def least_budget(self) -> Sli | None:
        """The SLI with the least budget left: the tile's headline."""
        scored = [s for s in self.slis if s.budget_left is not None]
        return min(scored, key=lambda s: s.budget_left or 0.0) if scored else None

    @property
    def charts(self) -> dict[str, Any]:
        return {
            "burndown": {
                s.objective.key: {"title": s.objective.title, "points": s.burndown}
                for s in self.measured
                if s.burndown and s.status != "unavailable"
            }
        }


def tally(reports: Iterable[Report]) -> dict[str, int]:
    """How many apps are in each status, for the home tile."""
    out = dict.fromkeys(STATUSES, 0)
    for r in reports:
        out[r.status] += 1
    return out
