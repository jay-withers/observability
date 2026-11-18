"""The apps, and how each table's rows are tied to one of them.

Each table names an app its own way, so the tie is explicit here, as
`costs.AREAS` is for resource groups:

- `Deployments_CL`: `Repo`, the repo name, which is the app's slug.
- `OpsEvents_CL`: drift rows name the root, `<repo>/<directory>` (their
  `Repo` is not used: this repo's own checks write theirs as observability);
  heartbeats name the Container Apps job (`caj-<prefix>-dev-<job>`) or the
  check (`check: drift <repo>/<directory>`); expiry rows name the host.
- `Costs_CL`: the resource group, `rg-<prefix>-dev`.
- Container Apps' own logs: the workload name, `ca-<prefix>-dev` for the
  app and `caj-<prefix>-dev-<job>` for its jobs.

`prefix` is the repo's Terraform `project_name`. Rows that belong to no app
(the landing zone, Cloudflare, sessions, the cost feed) stay on the estate
views only.
"""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime

from .costs import Cost
from .deployments import Deploy, rate
from .ops import Event, FailedRun


@dataclass(frozen=True)
class App:
    slug: str
    title: str
    prefix: str
    hosts: tuple[str, ...] = ()

    @property
    def resource_group(self) -> str:
        return f"rg-{self.prefix}-dev"

    def owns_workload(self, name: str) -> bool:
        """A container app or job of this app's, by Container Apps' name."""
        name = name.lower()
        return name.startswith((f"ca-{self.prefix}-", f"caj-{self.prefix}-"))

    def owns_root(self, root: str) -> bool:
        """A Terraform root (`<repo>/<directory>`) in this app's repo."""
        return root == self.slug or root.startswith(f"{self.slug}/")


APPS: tuple[App, ...] = (
    App("gym-log", "Gym log", "gymlog", ("health.jaywithers.uk",)),
    App("finances", "Finances", "finances", ("finances.jaywithers.uk",)),
    App("career", "Career", "career"),
    App("market-agent", "Market agent", "marketagent", ("marketagent.jaywithers.uk",)),
    App("security-testing", "Security testing", "sectest"),
    App("repo-agent", "Repo agent", "repoagent"),
    App("observability", "Observability", "observability", ("observability.jaywithers.uk",)),
)

_BY_SLUG = {a.slug: a for a in APPS}


def find(slug: str) -> App | None:
    return _BY_SLUG.get(slug)


def deploys_for(app: App, deploys: Iterable[Deploy]) -> list[Deploy]:
    return [d for d in deploys if d.repo == app.slug]


def owns_event(app: App, e: Event) -> bool:
    if e.kind == "drift":
        return app.owns_root(e.name)
    if e.kind == "heartbeat":
        if e.name.startswith("check: drift "):
            return app.owns_root(e.name.removeprefix("check: drift "))
        return app.owns_workload(e.name)
    if e.kind == "expiry":
        return e.name in app.hosts
    return False


def events_for(app: App, events: Iterable[Event]) -> list[Event]:
    return [e for e in events if owns_event(app, e)]


def failed_runs_for(app: App, runs: list[FailedRun] | None) -> list[FailedRun] | None:
    return None if runs is None else [r for r in runs if app.owns_workload(r.job)]


def costs_for(app: App, costs: Iterable[Cost]) -> list[Cost]:
    return [c for c in costs if c.resource_group == app.resource_group]


@dataclass
class Summary:
    """One app's card on the index."""

    app: App
    deploys: int
    success_pct: float | None
    last: Deploy | None
    problems: int | None
    spend: float | None
    errors: int | None


def summarise(
    app: App,
    deploys: list[Deploy],
    since: datetime,
    current: Iterable[Event] | None,
    spend: float | None,
    error_totals: dict[str, int] | None,
) -> Summary:
    """`deploys` newest first; `since` the range's first midnight
    (`deployments.range_start`); `current` each check's latest row; `spend`
    the app's for the range; `error_totals` error lines per workload over
    the range. None means that query failed."""
    mine = deploys_for(app, deploys)
    in_range = [d for d in mine if d.when >= since]
    errors = None
    if error_totals is not None:
        errors = sum(n for w, n in error_totals.items() if app.owns_workload(w))
    return Summary(
        app=app,
        deploys=len(in_range),
        success_pct=rate(sum(d.succeeded for d in in_range), len(in_range)),
        last=mine[0] if mine else None,
        problems=None if current is None else sum(e.problem for e in events_for(app, current)),
        spend=spend,
        errors=errors,
    )
