"""The pages."""

from __future__ import annotations

import logging
import threading
from datetime import UTC, datetime
from functools import lru_cache
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Form, Query, Request, status
from fastapi.responses import HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates

from .. import deployments
from ..logs import CachedLogs, LogsSource, QueryFailed
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
RANGES = (7, 30, 90)
DEFAULT_RANGE = 30

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
        return templates.TemplateResponse(
            request,
            "error.html",
            _context(request, "deployments", days, error=str(exc)),
            status_code=status.HTTP_502_BAD_GATEWAY,
        )
    view = deployments.build(deployments.parse(rows), days, datetime.now(UTC))
    return templates.TemplateResponse(
        request, "deployments.html", _context(request, "deployments", days, view=view)
    )
