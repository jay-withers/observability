"""The Costs view, from `Costs_CL` and the session and cost-feed checks.

`schedule-costs` posts the last seven days per resource group and service
every day, because Azure revises a day for about three. So a day has several
rows, and the latest post wins: the KQL keeps one row per (UsageDate,
ResourceGroup, ServiceName) with `arg_max`. Figures are ActualCost, before
any credit.

**Areas are fixed groups of resource groups**, so each keeps its colour
whatever the month's ranking. A new resource group falls into "landing zone
and other" until it is added to `AREAS`.
"""

from __future__ import annotations

import calendar
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, timedelta
from typing import Any

from .logs import Row
from .ops import Event
from .times import parse_time

AREAS: dict[str, str] = {
    "rg-platform-dev": "platform",
    "rg-gymlog-dev": "apps",
    "rg-career-dev": "apps",
    "rg-finances-dev": "apps",
    "rg-sectest-dev": "apps",
    "rg-repoagent-dev": "apps",
    "rg-observability-dev": "apps",
    "rg-marketagent-dev": "market-agent",
    "rg-aks-dev": "aks",
}
OTHER = "landing zone and other"
# The order areas are stacked and coloured in. Colour follows this order,
# never the month's ranking.
AREA_ORDER = ("platform", "apps", "market-agent", "aks", OTHER)

KQL = """Costs_CL
| summarize arg_max(TimeGenerated, Cost, Currency) by UsageDate, ResourceGroup, ServiceName
| project UsageDate, ResourceGroup, ServiceName, Cost, Currency"""

SYMBOLS = {"USD": "$", "GBP": "£", "EUR": "€"}


def area(resource_group: str) -> str:
    return AREAS.get(resource_group.lower(), OTHER)


def window(days: int) -> timedelta:
    """The range and the one before it (for the delta), or this month,
    whichever is longer, plus the week a day's rows keep being re-posted
    for, so the latest post of each day is in it."""
    return timedelta(days=max(2 * days, 31) + 8)


def billing_currency(costs: list[Cost]) -> str:
    """The currency most rows are in. Summing two would be meaningless."""
    counts: dict[str, int] = defaultdict(int)
    for c in costs:
        counts[c.currency] += 1
    return max(sorted(counts), key=lambda k: counts[k]) if counts else ""


def symbol(currency: str) -> str:
    return SYMBOLS.get(currency, f"{currency} " if currency else "")


def spend(costs: list[Cost], end: date, days: int) -> float:
    """The range's total in the billing currency, as `build` counts it."""
    currency = billing_currency(costs)
    start = end - timedelta(days=days - 1)
    return sum(c.cost for c in costs if c.currency == currency and start <= c.day <= end)


def last_day(costs: list[Cost], today: date) -> date:
    """The newest day with data, never after today. The feed lags a day or
    two, so a range ending today would end in days that read as no spend."""
    return min(today, max((c.day for c in costs), default=today))


@dataclass(frozen=True)
class Cost:
    day: date
    resource_group: str
    service: str
    cost: float
    currency: str

    @property
    def area(self) -> str:
        return area(self.resource_group)


def parse(rows: list[Row]) -> list[Cost]:
    costs = []
    for row in rows:
        when = parse_time(row.get("UsageDate"))
        if when is None:
            continue
        day = when.astimezone(UTC).date()
        value = row.get("Cost")
        if not isinstance(value, int | float):
            continue
        costs.append(
            Cost(
                day=day,
                resource_group=str(row.get("ResourceGroup") or "(none)").lower(),
                service=str(row.get("ServiceName") or "(none)"),
                cost=float(value),
                currency=str(row.get("Currency") or ""),
            )
        )
    return costs


@dataclass
class Bar:
    label: str
    value: float
    width: float
    area: str = ""


@dataclass
class View:
    days: int
    now: datetime
    symbol: str
    month_label: str
    month_to_date: float
    forecast: float | None
    # The selected range: total, the range before it, and the dates covered.
    range_start: date
    range_end: date
    total: float
    prior_total: float | None
    delta: str | None
    tone: str
    budget: float
    budget_used: float
    data_through: date | None
    left_out: int
    groups: list[Bar]
    services: list[Bar]
    sessions: list[Event]
    feed: Event | None
    charts: dict[str, Any] = field(default_factory=dict)

    @property
    def running(self) -> int:
        return sum(1 for e in self.sessions if e.status == "running")

    def scale(self, value: float) -> float:
        """A value as a percentage of the gauge's track."""
        top = max(self.budget, self.forecast or 0.0, self.month_to_date) * 1.05
        return 100.0 * value / top if top else 0.0


def build(
    costs: list[Cost],
    ops: dict[tuple[str, str], Event],
    days: int,
    now: datetime,
    budget: float,
    end: date | None = None,
) -> View:
    """`end` is the range's last day; by default the last day with data. An
    app's page passes the estate's, so a quiet app's range isn't shifted."""
    today = now.astimezone(UTC).date()
    # One currency: summing two would be meaningless. The billing currency is
    # the one most rows are in; any others are counted and left out.
    currency = billing_currency(costs)
    left_out = sum(1 for c in costs if c.currency != currency)
    costs = [c for c in costs if c.currency == currency]

    month_start = today.replace(day=1)
    month = [c for c in costs if month_start <= c.day <= today]
    mtd = sum(c.cost for c in month)

    # Grafana's forecast: the month's spend so far, per day of data, times the
    # days in the month. Days of data, not days elapsed: the feed lags a day
    # or two, and dividing by today's date would understate it.
    month_through = max((c.day for c in month), default=None)
    forecast = None
    if month_through is not None:
        days_with_data = (month_through - month_start).days + 1
        forecast = mtd / days_with_data * calendar.monthrange(today.year, today.month)[1]

    sym = symbol(currency)

    end = end or last_day(costs, today)
    start = end - timedelta(days=days - 1)
    prior_start = start - timedelta(days=days)
    in_range = [c for c in costs if start <= c.day <= end]
    total = sum(c.cost for c in in_range)
    # A prior total only when the data reaches back that far: a range before
    # the table's retention would read as a fall from zero.
    prior_total = None
    if costs and min(c.day for c in costs) <= prior_start:
        prior_total = sum(c.cost for c in costs if prior_start <= c.day < start)
    delta, tone = None, "neutral"
    if prior_total is not None:
        diff = total - prior_total
        delta = f"{'+' if diff >= 0 else '-'}{sym}{abs(diff):,.2f} vs prior {days}d"
        if round(diff, 2):
            tone = "good" if diff < 0 else "bad"

    by_group: dict[str, float] = defaultdict(float)
    by_service: dict[str, float] = defaultdict(float)
    for c in in_range:
        by_group[c.resource_group] += c.cost
        by_service[c.service] += c.cost

    sessions = sorted(
        (e for (k, _), e in ops.items() if k == "session"),
        key=lambda e: (e.status != "running", e.name),
    )
    return View(
        days=days,
        now=now,
        symbol=sym,
        month_label=today.strftime("%B"),
        month_to_date=mtd,
        forecast=forecast,
        range_start=start,
        range_end=end,
        total=total,
        prior_total=prior_total,
        delta=delta,
        tone=tone,
        budget=budget,
        budget_used=100.0 * mtd / budget if budget else 0.0,
        # The newest day with any data: early in a month the feed's lag
        # leaves this month empty, and the page should say "through 31 Oct"
        # rather than look broken.
        data_through=max((c.day for c in costs if c.day <= today), default=None),
        left_out=left_out,
        groups=_bars(by_group, area_of=area),
        services=_bars(by_service, limit=8),
        sessions=sessions,
        feed=ops.get(("cost-feed", "Costs_CL")),
        charts={
            "spend": _daily(costs, end, days),
            "treemap": _treemap(in_range),
            "areas": list(AREA_ORDER),
        },
    )


def _bars(totals: dict[str, float], limit: int = 0, area_of: Any = None) -> list[Bar]:
    ranked = sorted(totals.items(), key=lambda kv: -kv[1])
    if limit and len(ranked) > limit:
        rest = sum(v for _, v in ranked[limit - 1 :])
        ranked = [*ranked[: limit - 1], ("everything else", rest)]
    top = max((v for _, v in ranked), default=0.0)
    return [
        Bar(
            label=label,
            value=value,
            width=100.0 * value / top if top else 0.0,
            area=area_of(label) if area_of else "",
        )
        for label, value in ranked
        if value > 0
    ]


def _daily(costs: list[Cost], end: date, days: int) -> dict[str, Any]:
    dates = [end - timedelta(days=i) for i in range(days - 1, -1, -1)]
    index = {d: i for i, d in enumerate(dates)}
    series = {a: [0.0] * len(dates) for a in AREA_ORDER}
    for c in costs:
        i = index.get(c.day)
        if i is not None:
            series[c.area][i] += c.cost
    return {
        "dates": [d.isoformat() for d in dates],
        "series": {a: [round(v, 2) for v in values] for a, values in series.items()},
    }


def _treemap(month: list[Cost]) -> list[dict[str, Any]]:
    """Area > resource group > service, over the range."""
    tree: dict[str, dict[str, dict[str, float]]] = defaultdict(
        lambda: defaultdict(lambda: defaultdict(float))
    )
    for c in month:
        tree[c.area][c.resource_group][c.service] += c.cost
    nodes = []
    for a in AREA_ORDER:
        if a not in tree:
            continue
        groups = [
            {
                "name": rg,
                "children": [
                    {"name": svc, "value": round(v, 2)}
                    for svc, v in sorted(services.items(), key=lambda kv: -kv[1])
                    if v > 0
                ],
            }
            for rg, services in sorted(tree[a].items(), key=lambda kv: -sum(kv[1].values()))
        ]
        groups = [g for g in groups if g["children"]]
        if groups:
            nodes.append({"name": a, "children": groups})
    return nodes
