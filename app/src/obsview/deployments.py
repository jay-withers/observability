"""The Deployments view, from `Deployments_CL` rows to everything on the page.

The rows are written by workflows' `container-apps-deploy.yml`, one per
deploy. Everything here is a pure function of those rows and `now`, so the
tests pin the arithmetic that used to live in Grafana panel KQL.

"Succeeded" means `success` or `untested`, as the Grafana panels counted it:
an untested deploy is one with no smoke test, and it did go out.
"""

from __future__ import annotations

import statistics
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, time, timedelta
from itertools import pairwise
from typing import Any

from .logs import Row
from .sparkline import sparkline

OUTCOMES = ("success", "untested", "rolled_back", "failed")
SUCCEEDED = frozenset({"success", "untested"})
# The calendar always shows this many days, whatever the range: a heatmap of
# one week is a single column.
CALENDAR_DAYS = 91
# Lead-time buckets, in minutes. The last bucket is open-ended.
LEAD_BUCKETS = (5, 10, 20, 40)


def kql(environment: str) -> str:
    """The one query behind the view. The window is the query's timespan."""
    return (
        "Deployments_CL\n"
        f'| where Environment == "{environment}"\n'
        "| project TimeGenerated, Repo, App, Version, PreviousVersion, Outcome, Trigger,\n"
        "          LeadTimeSec, CommitSha, RunUrl\n"
        "| order by TimeGenerated desc\n"
        "| take 20000"
    )


def window(days: int) -> timedelta:
    """How far back the query reaches: the range, the range before it (for
    the deltas), and the calendar, whichever is longest."""
    return timedelta(days=max(2 * days, CALENDAR_DAYS))


@dataclass(frozen=True)
class Deploy:
    when: datetime
    repo: str
    app: str
    version: str
    previous_version: str
    outcome: str
    trigger: str
    lead_time_sec: float | None
    commit: str
    run_url: str

    @property
    def succeeded(self) -> bool:
        return self.outcome in SUCCEEDED

    @property
    def label(self) -> str:
        """`repo` alone where the app is named after it, else `repo / app`."""
        return self.repo if self.app in ("", self.repo) else f"{self.repo} / {self.app}"


def parse(rows: list[Row]) -> list[Deploy]:
    deploys = []
    for row in rows:
        when = row.get("TimeGenerated")
        if isinstance(when, str):
            when = datetime.fromisoformat(when.replace("Z", "+00:00"))
        if not isinstance(when, datetime):
            continue
        if when.tzinfo is None:
            when = when.replace(tzinfo=UTC)
        lead = row.get("LeadTimeSec")
        deploys.append(
            Deploy(
                when=when,
                repo=str(row.get("Repo") or ""),
                app=str(row.get("App") or ""),
                version=str(row.get("Version") or ""),
                previous_version=str(row.get("PreviousVersion") or ""),
                outcome=str(row.get("Outcome") or "unknown"),
                trigger=str(row.get("Trigger") or "unknown"),
                lead_time_sec=float(lead) if isinstance(lead, int | float) else None,
                commit=str(row.get("CommitSha") or "")[:7],
                run_url=str(row.get("RunUrl") or ""),
            )
        )
    deploys.sort(key=lambda d: d.when, reverse=True)
    return deploys


@dataclass
class Kpi:
    label: str
    value: str
    spark: str = ""
    # e.g. "+3 vs prior 30d". None when there is nothing to compare with.
    delta: str | None = None
    # "good", "bad" or "neutral", for colouring the delta.
    tone: str = "neutral"


@dataclass
class AppRow:
    label: str
    deploys: int
    success_pct: float
    rolled_back: int
    failed: int
    median_lead: str
    last: Deploy


@dataclass
class View:
    days: int
    now: datetime
    total: int
    kpis: list[Kpi]
    apps: list[AppRow]
    recent: list[Deploy]
    charts: dict[str, Any] = field(default_factory=dict)


def build(deploys: list[Deploy], days: int, now: datetime) -> View:
    # The range is whole UTC days ending today, so the tiles, the daily bars
    # and the sparklines all count the same deploys. A rolling `now - days`
    # would touch days + 1 dates and leave the oldest partial one out of the
    # charts while the tiles counted it.
    today = now.astimezone(UTC).date()
    dates = [today - timedelta(days=i) for i in range(days - 1, -1, -1)]
    current_start = datetime.combine(dates[0], time.min, tzinfo=UTC)
    previous_start = current_start - timedelta(days=days)
    current = [d for d in deploys if current_start <= d.when <= now]
    previous = [d for d in deploys if previous_start <= d.when < current_start]
    by_day: dict[date, list[Deploy]] = defaultdict(list)
    for d in current:
        by_day[d.when.astimezone(UTC).date()].append(d)
    per_day = [by_day.get(day, []) for day in dates]

    return View(
        days=days,
        now=now,
        total=len(current),
        kpis=_kpis(current, previous, per_day, days),
        apps=_apps(current),
        recent=current[:20],
        charts={
            "start": js_time(current_start),
            "dates": [d.isoformat() for d in dates],
            "outcomes": {
                o: [sum(1 for d in ds if d.outcome == o) for ds in per_day] for o in OUTCOMES
            },
            "triggers": [
                {"name": name, "value": n}
                for name, n in Counter(d.trigger for d in current).most_common()
            ],
            "calendar": _calendar(deploys, now),
            "timeline": _timeline(current),
            "lead": _lead_histogram(current),
        },
    )


# --- tiles --------------------------------------------------------------------


def _rate(part: int, whole: int) -> float | None:
    return 100.0 * part / whole if whole else None


def _median_lead(deploys: list[Deploy]) -> float | None:
    leads = [d.lead_time_sec for d in deploys if d.lead_time_sec is not None]
    return statistics.median(leads) if leads else None


def _kpis(
    current: list[Deploy], previous: list[Deploy], per_day: list[list[Deploy]], days: int
) -> list[Kpi]:
    prior = f"vs prior {days}d"

    def count_delta(now_n: int, before_n: int, lower_is_better: bool | None) -> tuple[str, str]:
        diff = now_n - before_n
        tone = "neutral"
        if diff and lower_is_better is not None:
            tone = "good" if (diff < 0) == lower_is_better else "bad"
        return f"{diff:+d} {prior}", tone

    def pct_delta(now_v: float | None, before_v: float | None, higher_is_better: bool | None):
        if now_v is None or before_v is None:
            return None, "neutral"
        diff = now_v - before_v
        tone = "neutral"
        if round(diff, 1) and higher_is_better is not None:
            tone = "good" if (diff > 0) == higher_is_better else "bad"
        return f"{diff:+.1f} pts {prior}", tone

    counts = [float(len(ds)) for ds in per_day]
    n_now, n_before = len(current), len(previous)

    ok_now = sum(d.succeeded for d in current)
    ok_before = sum(d.succeeded for d in previous)
    rate_now, rate_before = _rate(ok_now, n_now), _rate(ok_before, n_before)

    rb_now = sum(d.outcome == "rolled_back" for d in current)
    rb_before = sum(d.outcome == "rolled_back" for d in previous)
    failed_now = sum(d.outcome == "failed" for d in current)
    failed_before = sum(d.outcome == "failed" for d in previous)

    lead_now, lead_before = _median_lead(current), _median_lead(previous)
    reno_now = _rate(sum(d.trigger == "renovate" for d in current), n_now)
    reno_before = _rate(sum(d.trigger == "renovate" for d in previous), n_before)

    kpis = []
    delta, tone = count_delta(n_now, n_before, None)
    kpis.append(Kpi("Deploys", str(n_now), sparkline(counts), delta, tone))

    delta_p, tone = pct_delta(rate_now, rate_before, True)
    kpis.append(
        Kpi(
            "Success rate",
            _fmt_pct(rate_now),
            sparkline([_rate(sum(d.succeeded for d in ds), len(ds)) for ds in per_day]),
            delta_p,
            tone,
        )
    )

    delta, tone = count_delta(rb_now, rb_before, True)
    kpis.append(
        Kpi(
            "Rolled back",
            str(rb_now),
            sparkline([float(sum(d.outcome == "rolled_back" for d in ds)) for ds in per_day]),
            delta,
            tone,
        )
    )
    delta, tone = count_delta(failed_now, failed_before, True)
    kpis.append(
        Kpi(
            "Failed",
            str(failed_now),
            sparkline([float(sum(d.outcome == "failed" for d in ds)) for ds in per_day]),
            delta,
            tone,
        )
    )

    lead_delta, lead_tone = None, "neutral"
    if lead_now is not None and lead_before is not None:
        diff = lead_now - lead_before
        lead_delta = f"{'+' if diff >= 0 else '-'}{fmt_duration(abs(diff))} {prior}"
        if abs(diff) >= 1:
            lead_tone = "good" if diff < 0 else "bad"
    kpis.append(
        Kpi(
            "Median lead time",
            fmt_duration(lead_now) if lead_now is not None else "–",
            sparkline([_median_lead(ds) for ds in per_day]),
            lead_delta,
            lead_tone,
        )
    )

    delta_p, tone = pct_delta(reno_now, reno_before, None)
    kpis.append(
        Kpi(
            "Renovate share",
            _fmt_pct(reno_now),
            sparkline([_rate(sum(d.trigger == "renovate" for d in ds), len(ds)) for ds in per_day]),
            delta_p,
            tone,
        )
    )
    return kpis


def _fmt_pct(value: float | None) -> str:
    return "–" if value is None else f"{value:.0f}%" if value in (0, 100) else f"{value:.1f}%"


def js_time(when: datetime) -> str:
    """ISO 8601 that every browser's `Date` parses: milliseconds and `Z`.

    Python's `isoformat()` gives microseconds and `+00:00`, which is outside
    ECMAScript's date-time format, so a strict engine returns Invalid Date.
    """
    return when.astimezone(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def fmt_duration(seconds: float) -> str:
    seconds = round(seconds)
    if seconds < 60:
        return f"{seconds}s"
    minutes, secs = divmod(seconds, 60)
    if minutes < 60:
        return f"{minutes}m {secs:02d}s" if minutes < 10 and secs else f"{minutes}m"
    hours, minutes = divmod(minutes, 60)
    return f"{hours}h {minutes:02d}m"


def fmt_ago(when: datetime, now: datetime) -> str:
    seconds = (now - when).total_seconds()
    if seconds < 60:
        return "just now"
    if seconds < 3600:
        return f"{int(seconds // 60)}m ago"
    if seconds < 86400:
        return f"{int(seconds // 3600)}h ago"
    return f"{int(seconds // 86400)}d ago"


# --- tables -------------------------------------------------------------------


def _apps(current: list[Deploy]) -> list[AppRow]:
    groups: dict[str, list[Deploy]] = defaultdict(list)
    for d in current:  # newest first, so each group's [0] is its last deploy
        groups[d.label].append(d)
    rows = []
    for label, ds in groups.items():
        lead = _median_lead(ds)
        rows.append(
            AppRow(
                label=label,
                deploys=len(ds),
                success_pct=_rate(sum(d.succeeded for d in ds), len(ds)) or 0.0,
                rolled_back=sum(d.outcome == "rolled_back" for d in ds),
                failed=sum(d.outcome == "failed" for d in ds),
                median_lead=fmt_duration(lead) if lead is not None else "–",
                last=ds[0],
            )
        )
    rows.sort(key=lambda r: r.last.when, reverse=True)
    return rows


# --- charts -------------------------------------------------------------------


def _calendar(deploys: list[Deploy], now: datetime) -> dict[str, Any]:
    end = now.astimezone(UTC).date()
    start = end - timedelta(days=CALENDAR_DAYS - 1)
    counts = Counter(d.when.astimezone(UTC).date() for d in deploys)
    data = [
        [(start + timedelta(days=i)).isoformat(), counts.get(start + timedelta(days=i), 0)]
        for i in range(CALENDAR_DAYS)
    ]
    return {"start": start.isoformat(), "end": end.isoformat(), "data": data}


def _timeline(current: list[Deploy]) -> dict[str, Any]:
    """One row per app, ordered by deploy count so the busiest sit at the top."""
    counts = Counter(d.label for d in current)
    # Reversed: ECharts draws the first category at the bottom.
    apps = [label for label, _ in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))][::-1]
    index = {label: i for i, label in enumerate(apps)}
    points = [
        [js_time(d.when), index[d.label], d.outcome, d.version, d.trigger]
        for d in reversed(current)
    ]
    return {"apps": apps, "points": points}


def _lead_histogram(current: list[Deploy]) -> dict[str, Any]:
    edges = (0, *LEAD_BUCKETS)
    labels = [f"{lo}–{hi}m" for lo, hi in pairwise(edges)]
    labels.append(f"{LEAD_BUCKETS[-1]}m+")
    counts = [0] * len(labels)
    for d in current:
        if d.lead_time_sec is None:
            continue
        minutes = d.lead_time_sec / 60
        slot = next((i for i, hi in enumerate(LEAD_BUCKETS) if minutes < hi), len(LEAD_BUCKETS))
        counts[slot] += 1
    return {"labels": labels, "counts": counts}
