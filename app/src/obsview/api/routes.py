"""The pages."""

from __future__ import annotations

import logging
import threading
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta
from functools import lru_cache
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Form, HTTPException, Query, Request, status
from fastapi.responses import HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates

from .. import apps, costs, deployments, errors, ops
from ..logs import CachedLogs, LogsSource, QueryFailed, Row
from ..settings import settings
from . import deps

logger = logging.getLogger(__name__)

PACKAGE_DIR = Path(__file__).resolve().parent.parent
TEMPLATE_DIR = PACKAGE_DIR / "templates"
STATIC_DIR = PACKAGE_DIR / "static"

templates = Jinja2Templates(directory=str(TEMPLATE_DIR))
templates.env.globals["ago"] = deployments.fmt_ago
templates.env.globals["duration"] = deployments.fmt_duration
templates.env.globals["js_time"] = deployments.js_time

# The ranges on offer. Anything else in `?days=` falls back to the default
# rather than erroring: it is a bookmark, not an API.
RANGES = (1, 3, 5, 7, 30, 60, 90)
DEFAULT_RANGE = 7


# /readyz's query: the default page's deploy query, which the start-up warm-up has
# usually already cached. The shared smoke test calls /readyz once, with no
# retry, so it should land on the cache rather than a cold query; a cold one
# still proves the identity can read the table.
def ready_query() -> tuple[str, timedelta]:
    return deployments.kql(settings().environment), deployments.window(DEFAULT_RANGE)


public = APIRouter()
router = APIRouter()


_source_lock = threading.Lock()


@lru_cache(maxsize=1)
def _build_source() -> LogsSource:
    cfg = settings()
    if cfg.use_demo:
        from ..demo import DemoLogs

        return DemoLogs()
    from ..logs import AzureLogs

    return CachedLogs(AzureLogs(cfg.workspace_id, cfg.azure_client_id), cfg.cache_ttl_seconds)


def source() -> LogsSource:
    """The process-wide (cached) source of rows. Replaced in tests.

    Locked: `lru_cache` does not serialise misses, so the start-up warm
    thread and the first request could each build a cache, and the warm-up
    would fill the one that is thrown away.
    """
    with _source_lock:
        return _build_source()


def warm() -> None:
    """Fill the cache for the default page (/apps at the default range), so
    the first open after a start is fast."""
    for key, result in _gather(_index_queries(DEFAULT_RANGE, datetime.now(UTC))).items():
        if isinstance(result, QueryFailed):
            logger.warning("cache warm-up of %s failed; the first page load will retry", key)


def _context(request: Request, page: str, days: int, **extra: Any) -> dict[str, Any]:
    return {
        "page": page,
        "days": days,
        "ranges": RANGES,
        "demo": settings().use_demo,
        "environment": settings().environment,
        "app_slugs": [a.slug for a in apps.APPS],
        **extra,
    }


# --- login --------------------------------------------------------------------


@public.get("/login", response_class=HTMLResponse, include_in_schema=False)
def login_form(request: Request) -> Any:
    return templates.TemplateResponse(request, "login.html", {"error": "", "page": "login"})


@public.post("/login", include_in_schema=False)
def login(request: Request, passcode: str = Form(default="")) -> Any:
    if not deps.check_passcode(passcode):
        return templates.TemplateResponse(
            request,
            "login.html",
            {"error": "That is not it.", "page": "login"},
            status_code=status.HTTP_401_UNAUTHORIZED,
        )
    response = RedirectResponse("/", status_code=status.HTTP_303_SEE_OTHER)
    response.set_cookie(
        deps.COOKIE_NAME,
        deps.issue(),
        max_age=settings().cookie_max_age_seconds,
        httponly=True,
        samesite="lax",
        secure=True,
    )
    return response


@public.get("/manifest.json", include_in_schema=False)
def manifest() -> dict[str, Any]:
    return {
        "name": "Observability",
        "short_name": "Obs",
        "start_url": "/",
        "display": "standalone",
        "background_color": "#0e1116",
        "theme_color": "#0e1116",
        "icons": [{"src": "/static/icon.svg", "sizes": "any", "type": "image/svg+xml"}],
    }


# --- pages --------------------------------------------------------------------


@router.get("/", include_in_schema=False)
def home() -> RedirectResponse:
    return RedirectResponse("/apps", status_code=status.HTTP_303_SEE_OTHER)


# The estate-wide views this replaced, kept as redirects so bookmarks land
# somewhere: deploys are all apps', ops mostly the platform's, and costs the
# whole subscription's. Not 301: browsers keep a permanent redirect for good,
# and the mapping may change.
_MOVED = {"/deployments": "/apps", "/ops": "/platform", "/costs": "/overall"}


def _moved(target: str) -> Any:
    def redirect(days: str = Query(default="")) -> RedirectResponse:
        return RedirectResponse(
            f"{target}?days={_range(days)}", status_code=status.HTTP_303_SEE_OTHER
        )

    return redirect


for _old, _new in _MOVED.items():
    router.add_api_route(_old, _moved(_new), methods=["GET"], include_in_schema=False)


def _range(raw: str) -> int:
    """`?days=` as one of RANGES. A str, not an int, so `?days=30d` or an
    empty value falls back to the default instead of FastAPI's 422."""
    try:
        days = int(raw)
    except ValueError:
        return DEFAULT_RANGE
    return days if days in RANGES else DEFAULT_RANGE


def _failed(request: Request, page: str, days: int, exc: QueryFailed) -> Any:
    return templates.TemplateResponse(
        request,
        "error.html",
        _context(request, page, days, error=str(exc)),
        status_code=status.HTTP_502_BAD_GATEWAY,
    )


def _gather(specs: dict[str, tuple[str, timedelta]]) -> dict[str, list[Row] | QueryFailed]:
    """Every query at once, each result or its failure, so a cold page waits
    for the slowest rather than the sum, and the page decides which failures
    it can render around."""
    with ThreadPoolExecutor(max_workers=len(specs)) as pool:
        jobs = {key: pool.submit(source().query, *spec) for key, spec in specs.items()}
        out: dict[str, list[Row] | QueryFailed] = {}
        for key, job in jobs.items():
            try:
                out[key] = job.result()
            except QueryFailed as exc:
                out[key] = exc
        return out


def _ok(result: list[Row] | QueryFailed, what: str) -> list[Row] | None:
    """A secondary query's rows, or None (logged) if it failed: its card says
    so and the rest of the page renders, as Grafana failed one panel, not all."""
    if isinstance(result, QueryFailed):
        logger.warning("%s query failed; rendering without it: %s", what, result)
        return None
    return result


# --- apps and the platform -----------------------------------------------------


def _app_queries(span: int, now: datetime) -> dict[str, tuple[str, timedelta]]:
    """One set of queries and windows for every page, so the apps index, each
    app and the platform read the same cache entries at a range."""
    env = settings().environment
    return {
        "deploys": (deployments.kql(env), deployments.window(span)),
        "ops": (ops.kql(env), ops.window(span)),
        "failed": (ops.FAILED_RUNS_KQL, timedelta(days=span)),
        "costs": (costs.KQL, costs.window(span)),
        "errors": (errors.KQL, errors.window(deployments.range_start(span, now), now)),
    }


def _index_queries(span: int, now: datetime) -> dict[str, tuple[str, timedelta]]:
    """The apps index's: every query but failed runs, which it doesn't show."""
    specs = _app_queries(span, now)
    del specs["failed"]
    return specs


def _cost_end(all_costs: list[costs.Cost], now: datetime) -> date:
    # The estate's last day with data, not the app's, so an app with no
    # spend for a few days isn't shown an older range.
    return costs.last_day(all_costs, now.astimezone(UTC).date())


def _errors(
    rows: list[Row] | None, owns: Callable[[str], bool], span: int, now: datetime
) -> tuple[list[errors.ErrorGroup] | None, int]:
    """The grouped errors of the workloads `owns` picks, and their true total.
    None when the query failed."""
    if rows is None:
        return None, 0
    since = deployments.range_start(span, now)
    lines = [e for e in errors.parse(rows) if owns(e.workload) and e.when >= since]
    return errors.group(lines), sum(n for w, n in errors.totals(rows).items() if owns(w))


@router.get("/apps", response_class=HTMLResponse, include_in_schema=False)
def apps_page(request: Request, days: str = Query(default="")) -> Any:
    span = _range(days)
    now = datetime.now(UTC)
    got = _gather(_index_queries(span, now))
    if isinstance(got["deploys"], QueryFailed):
        return _failed(request, "apps", span, got["deploys"])
    deploys = deployments.parse(got["deploys"])
    ops_rows = _ok(got["ops"], "ops")
    cost_rows = _ok(got["costs"], "costs")
    error_rows = _ok(got["errors"], "errors")
    current = None if ops_rows is None else list(ops.latest(ops.parse(ops_rows), now).values())
    totals = None if error_rows is None else errors.totals(error_rows)
    all_costs = None if cost_rows is None else costs.parse(cost_rows)
    end = None if all_costs is None else _cost_end(all_costs, now)
    since = deployments.range_start(span, now)
    summaries = []
    for app in apps.APPS:
        spend = None
        if all_costs is not None and end is not None:
            spend = costs.spend(apps.costs_for(app, all_costs), end, span)
        summaries.append(apps.summarise(app, deploys, since, current, spend, totals))
    symbol = costs.symbol(costs.billing_currency(all_costs)) if all_costs else ""
    # Every deploy is an app's (only container-apps-deploy writes the table),
    # so the estate's deploy panels live here, under the cards.
    dview = deployments.build(deploys, span, now)
    return templates.TemplateResponse(
        request,
        "apps.html",
        _context(
            request,
            "apps",
            span,
            summaries=summaries,
            symbol=symbol,
            now=now,
            dview=dview,
            payload={
                "groups": {"deploy": dview.charts},
                "now": deployments.js_time(now),
                "days": span,
            },
        ),
    )


@dataclass(frozen=True)
class _Slice:
    """Which rows a page shows: one app's, or the platform's (no app's)."""

    events: Callable[[list[ops.Event]], list[ops.Event]]
    runs: Callable[[list[ops.FailedRun] | None], list[ops.FailedRun] | None]
    costs: Callable[[list[costs.Cost]], list[costs.Cost]]
    workload: Callable[[str], bool]


def _panels(
    got: dict[str, list[Row] | QueryFailed], pick: _Slice, span: int, now: datetime
) -> dict[str, Any]:
    """The ops, costs and errors panels shared by an app's page and the
    platform's, over the rows `pick` keeps. A failed query leaves its view
    None, and its card says so."""
    ops_rows, failed_rows = _ok(got["ops"], "ops"), _ok(got["failed"], "failed-runs")
    oview = None
    if ops_rows is not None:
        failed = None if failed_rows is None else ops.parse_failed_runs(failed_rows)
        oview = ops.build(pick.events(ops.parse(ops_rows)), pick.runs(failed), span, now)

    cost_rows = _ok(got["costs"], "costs")
    cview = None
    if cost_rows is not None:
        all_costs = costs.parse(cost_rows)
        end = _cost_end(all_costs, now)
        # Only the slice's rows, stacked by its own services: the budget and
        # the estate's areas are the Overall page's.
        cview = costs.build(
            pick.costs(all_costs), span, now, settings().budget, end=end, by_area=False
        )

    egroups, error_total = _errors(_ok(got["errors"], "errors"), pick.workload, span, now)
    groups: dict[str, Any] = {}
    if oview is not None:
        groups["ops"] = oview.charts
    if cview is not None:
        groups["costs"] = cview.charts
    return {
        "oview": oview,
        "cview": cview,
        "errors": egroups,
        "error_total": error_total,
        "error_cap": errors.PER_WORKLOAD,
        "now": now,
        "payload": {
            "groups": groups,
            "now": deployments.js_time(now),
            "days": span,
            "symbol": cview.symbol if cview else "",
        },
    }


@router.get("/apps/{slug}", response_class=HTMLResponse, include_in_schema=False)
def app_page(request: Request, slug: str, days: str = Query(default="")) -> Any:
    app = apps.find(slug)
    if app is None:
        raise HTTPException(status.HTTP_404_NOT_FOUND, "No such app")
    span = _range(days)
    now = datetime.now(UTC)
    got = _gather(_app_queries(span, now))
    if isinstance(got["deploys"], QueryFailed):
        return _failed(request, "apps", span, got["deploys"])
    dview = deployments.build(apps.deploys_for(app, deployments.parse(got["deploys"])), span, now)
    pick = _Slice(
        events=lambda es: apps.events_for(app, es),
        runs=lambda rs: apps.failed_runs_for(app, rs),
        costs=lambda cs: apps.costs_for(app, cs),
        workload=app.owns_workload,
    )
    panels = _panels(got, pick, span, now)
    panels["payload"]["groups"] = {"deploy": dview.charts, **panels["payload"]["groups"]}
    return templates.TemplateResponse(
        request, "app.html", _context(request, "apps", span, app=app, dview=dview, **panels)
    )


_PLATFORM = _Slice(
    events=apps.platform_events,
    runs=apps.platform_failed_runs,
    costs=apps.platform_costs,
    workload=lambda w: not apps.app_workload(w),
)

# Checks that write a row on every run, so no row in 48 hours means the check
# itself has stopped: said as a problem, not left as an absent section that
# reads like nothing running.
_ALWAYS_REPORT = ("session", "cost-feed")


@router.get("/platform", response_class=HTMLResponse, include_in_schema=False)
def platform_page(request: Request, days: str = Query(default="")) -> Any:
    """Everything no app owns. Every query here is optional: one that fails
    renders a "couldn't read" card and the rest of the page is current."""
    span = _range(days)
    now = datetime.now(UTC)
    specs = _app_queries(span, now)
    del specs["deploys"]  # no platform deploys are recorded
    panels = _panels(_gather(specs), _PLATFORM, span, now)
    oview = panels["oview"]
    silent = []
    if oview is not None:
        present = {s.kind for s in oview.sections}
        silent = [t for k, t, _ in ops.SECTIONS if k in _ALWAYS_REPORT and k not in present]
    return templates.TemplateResponse(
        request, "platform.html", _context(request, "platform", span, silent=silent, **panels)
    )


@router.get("/overall", response_class=HTMLResponse, include_in_schema=False)
def overall_page(request: Request, days: str = Query(default="")) -> Any:
    """The whole subscription's costs: the budget, and every resource group's
    spend broken down by area, group and service. The same query and window
    as the other pages, so they share the cache."""
    span = _range(days)
    now = datetime.now(UTC)
    kql, window = _app_queries(span, now)["costs"]
    got = _gather({"costs": (kql, window)})["costs"]
    if isinstance(got, QueryFailed):
        return _failed(request, "overall", span, got)
    all_costs = costs.parse(got)
    # The same end as the app and platform pages, so a range covers the same days.
    view = costs.build(all_costs, span, now, settings().budget, end=_cost_end(all_costs, now))
    payload = {
        "groups": {"costs": view.charts},
        "now": deployments.js_time(now),
        "days": span,
        "symbol": view.symbol,
    }
    return templates.TemplateResponse(
        request, "overall.html", _context(request, "overall", span, cview=view, payload=payload)
    )
