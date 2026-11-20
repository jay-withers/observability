"""The Costs panels, from `Costs_CL`.

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
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, timedelta
from typing import Any

from .logs import Row
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
# The series a slice's daily chart stacks: its top services, the rest folded
# into one, within the five chart colours.
SERVICE_SERIES = 5
REST = "everything else"

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
    # The 1-based chart colour, or 0 for the plain bar colour.
    colour: int = 0


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
    areas: list[Bar]
    charts: dict[str, Any] = field(default_factory=dict)

    def scale(self, value: float) -> float:
        """A value as a percentage of the gauge's track."""
        top = max(self.budget, self.forecast or 0.0, self.month_to_date) * 1.05
        return 100.0 * value / top if top else 0.0


def build(
    costs: list[Cost],
    days: int,
    now: datetime,
    budget: float,
    end: date | None = None,
    by_area: bool = True,
) -> View:
    """`end` is the range's last day; by default the last day with data. An
    app's page passes the estate's, so a quiet app's range isn't shifted.

    `by_area` is the whole estate's view: the daily chart stacks the fixed
    areas and the treemap starts at them. A slice (one app, the platform)
    passes False: its chart stacks its own top services and its treemap
    starts at its resource groups, so nothing on it names another area."""
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
    by_area_totals: dict[str, float] = defaultdict(float)
    for c in in_range:
        by_group[c.resource_group] += c.cost
        by_service[c.service] += c.cost
        by_area_totals[c.area] += c.cost

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
        groups=_bars(by_group, colour=_group_colour if by_area else None),
        services=_bars(by_service, limit=8),
        areas=_bars(by_area_totals, colour=_area_colour),
        charts=_charts(costs, in_range, end, days, by_area),
    )


def _area_colour(name: str) -> int:
    return AREA_ORDER.index(name) + 1


def _group_colour(resource_group: str) -> int:
    return _area_colour(area(resource_group))


def _charts(
    costs: list[Cost], in_range: list[Cost], end: date, days: int, by_area: bool
) -> dict[str, Any]:
    """The daily chart's series (`series_keys`, in colour order) and the
    treemap. `by_area` tells the page the treemap's top level is the areas,
    coloured as the chart's; a slice's is its resource groups."""
    if by_area:
        keys = list(AREA_ORDER)
        return {
            "spend": _daily(costs, end, days, keys, lambda c: c.area),
            "treemap": _treemap(in_range, AREA_ORDER, lambda c: c.area),
            "series_keys": keys,
            "by_area": True,
        }
    totals: dict[str, float] = defaultdict(float)
    for c in in_range:
        totals[c.service] += c.cost
    # Every service with a row, even one netting to zero or less (a credit),
    # so the stacked days still add up to the range's total.
    ranked = sorted(totals, key=lambda s: -totals[s])
    top = ranked if len(ranked) <= SERVICE_SERIES else ranked[: SERVICE_SERIES - 1]
    keys = top + ([REST] if len(ranked) > len(top) else [])
    chosen = set(top)
    groups = sorted({c.resource_group for c in in_range})
    return {
        "spend": _daily(
            costs, end, days, keys, lambda c: c.service if c.service in chosen else REST
        ),
        "treemap": _treemap(in_range, groups, lambda c: c.resource_group, nested=False),
        "series_keys": keys,
        "by_area": False,
    }


def _bars(
    totals: dict[str, float], limit: int = 0, colour: Callable[[str], int] | None = None
) -> list[Bar]:
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
            colour=colour(label) if colour else 0,
        )
        for label, value in ranked
        if value > 0
    ]


def _daily(
    costs: list[Cost], end: date, days: int, keys: list[str], key: Callable[[Cost], str]
) -> dict[str, Any]:
    dates = [end - timedelta(days=i) for i in range(days - 1, -1, -1)]
    index = {d: i for i, d in enumerate(dates)}
    series = {k: [0.0] * len(dates) for k in keys}
    for c in costs:
        i = index.get(c.day)
        k = key(c)
        if i is not None and k in series:
            series[k][i] += c.cost
    return {
        "dates": [d.isoformat() for d in dates],
        "series": {k: [round(v, 2) for v in values] for k, values in series.items()},
    }


def _treemap(
    in_range: list[Cost],
    order: Iterable[str],
    key: Callable[[Cost], str],
    nested: bool = True,
) -> list[dict[str, Any]]:
    """Over the range: `key` (an area) > resource group > service, or, not
    nested, resource group > service."""
    tree: dict[str, dict[str, dict[str, float]]] = defaultdict(
        lambda: defaultdict(lambda: defaultdict(float))
    )
    for c in in_range:
        tree[key(c)][c.resource_group][c.service] += c.cost

    def services(totals: dict[str, float]) -> list[dict[str, Any]]:
        return [
            {"name": svc, "value": round(v, 2)}
            for svc, v in sorted(totals.items(), key=lambda kv: -kv[1])
            if v > 0
        ]

    nodes = []
    for top in order:
        if top not in tree:
            continue
        groups = [
            {"name": rg, "children": services(svcs)}
            for rg, svcs in sorted(tree[top].items(), key=lambda kv: -sum(kv[1].values()))
        ]
        groups = [g for g in groups if g["children"]]
        if not nested:
            nodes.extend(groups)
        elif groups:
            nodes.append({"name": top, "children": groups})
    return nodes
