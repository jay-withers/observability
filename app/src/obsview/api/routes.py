"""The pages."""

from __future__ import annotations

import logging
import threading
from concurrent.futures import ThreadPoolExecutor
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
DEFAULT_RANGE = 30


# /readyz's query: the default view's own, which the start-up warm-up has
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
    """Fill the cache for the default page, so the first open after a start is fast."""
    try:
        source().query(deployments.kql(settings().environment), deployments.window(DEFAULT_RANGE))
    except QueryFailed:
        logger.warning("cache warm-up failed; the first page load will retry")


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
    return RedirectResponse("/deployments", status_code=status.HTTP_303_SEE_OTHER)


@router.get("/deployments", response_class=HTMLResponse, include_in_schema=False)
def deployments_page(request: Request, days: str = Query(default="")) -> Any:
    return _deployments(request, _range(days))


def _range(raw: str) -> int:
    """`?days=` as one of RANGES. A str, not an int, so `?days=30d` or an
    empty value falls back to the default instead of FastAPI's 422."""
    try:
        days = int(raw)
    except ValueError:
        return DEFAULT_RANGE
    return days if days in RANGES else DEFAULT_RANGE


def _deployments(request: Request, days: int) -> Any:
    cfg = settings()
    try:
        rows = source().query(deployments.kql(cfg.environment), deployments.window(days))
    except QueryFailed as exc:
        return _failed(request, "deployments", days, exc)
    view = deployments.build(deployments.parse(rows), days, datetime.now(UTC))
    return templates.TemplateResponse(
        request, "deployments.html", _context(request, "deployments", days, view=view)
    )


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


@router.get("/ops", response_class=HTMLResponse, include_in_schema=False)
def ops_page(request: Request, days: str = Query(default="")) -> Any:
    span = _range(days)
    env = settings().environment
    got = _gather(
        {
            "ops": (ops.kql(env), ops.window(span)),
            "failed": (ops.FAILED_RUNS_KQL, timedelta(days=span)),
        }
    )
    if isinstance(got["ops"], QueryFailed):
        return _failed(request, "ops", span, got["ops"])
    failed_rows = _ok(got["failed"], "failed-runs")
    failed = None if failed_rows is None else ops.parse_failed_runs(failed_rows)
    view = ops.build(ops.parse(got["ops"]), failed, span, datetime.now(UTC))
    return templates.TemplateResponse(
        request, "ops.html", _context(request, "ops", span, view=view)
    )


@router.get("/costs", response_class=HTMLResponse, include_in_schema=False)
def costs_page(request: Request, days: str = Query(default="")) -> Any:
    span = _range(days)
    cfg = settings()
    got = _gather(
        {
            "costs": (costs.KQL, costs.window(span)),
            "ops": (ops.kql(cfg.environment), ops.window(DEFAULT_RANGE)),
        }
    )
    rows, events = got["costs"], got["ops"]
    if isinstance(rows, QueryFailed):
        return _failed(request, "costs", span, rows)
    if isinstance(events, QueryFailed):
        return _failed(request, "costs", span, events)
    now = datetime.now(UTC)
    view = costs.build(costs.parse(rows), ops.latest(ops.parse(events), now), span, now, cfg.budget)
    return templates.TemplateResponse(
        request, "costs.html", _context(request, "costs", span, view=view)
    )


# --- per-app pages ------------------------------------------------------------


def _app_queries(span: int, now: datetime) -> dict[str, tuple[str, timedelta]]:
    """The estate views' own queries and windows, so an app page reads the
    same cache entries as /deployments, /ops and /costs at that range."""
    env = settings().environment
    return {
        "deploys": (deployments.kql(env), deployments.window(span)),
        "ops": (ops.kql(env), ops.window(span)),
        "failed": (ops.FAILED_RUNS_KQL, timedelta(days=span)),
        "costs": (costs.KQL, costs.window(span)),
        "errors": (errors.KQL, errors.window(deployments.range_start(span, now), now)),
    }


def _cost_end(all_costs: list[costs.Cost], now: datetime) -> date:
    # The estate's last day with data, not the app's, so an app with no
    # spend for a few days isn't shown an older range.
    return costs.last_day(all_costs, now.astimezone(UTC).date())


@router.get("/apps", response_class=HTMLResponse, include_in_schema=False)
def apps_page(request: Request, days: str = Query(default="")) -> Any:
    span = _range(days)
    now = datetime.now(UTC)
    specs = _app_queries(span, now)
    del specs["failed"]  # the index shows no failed runs
    got = _gather(specs)
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
    return templates.TemplateResponse(
        request,
        "apps.html",
        _context(request, "apps", span, summaries=summaries, symbol=symbol, now=now),
    )


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

    ops_rows, failed_rows = _ok(got["ops"], "ops"), _ok(got["failed"], "failed-runs")
    oview = None
    if ops_rows is not None:
        failed = None if failed_rows is None else ops.parse_failed_runs(failed_rows)
        oview = ops.build(
            apps.events_for(app, ops.parse(ops_rows)), apps.failed_runs_for(app, failed), span, now
        )

    cost_rows = _ok(got["costs"], "costs")
    cview = None
    if cost_rows is not None:
        all_costs = costs.parse(cost_rows)
        cview = costs.build(
            apps.costs_for(app, all_costs),
            {},
            span,
            now,
            settings().budget,
            end=_cost_end(all_costs, now),
        )

    error_rows = _ok(got["errors"], "errors")
    egroups, error_total = None, 0
    if error_rows is not None:
        since = deployments.range_start(span, now)
        egroups = errors.group(
            [
                e
                for e in errors.parse(error_rows)
                if app.owns_workload(e.workload) and e.when >= since
            ]
        )
        error_total = sum(n for w, n in errors.totals(error_rows).items() if app.owns_workload(w))

    groups: dict[str, Any] = {"deploy": dview.charts}
    if oview is not None:
        groups["ops"] = oview.charts
    if cview is not None:
        groups["costs"] = cview.charts
    return templates.TemplateResponse(
        request,
        "app.html",
        _context(
            request,
            "apps",
            span,
            app=app,
            dview=dview,
            oview=oview,
            cview=cview,
            errors=egroups,
            error_total=error_total,
            error_cap=errors.PER_WORKLOAD,
            now=now,
            payload={
                "groups": groups,
                "now": deployments.js_time(now),
                "days": span,
                "symbol": cview.symbol if cview else "",
            },
        ),
    )
