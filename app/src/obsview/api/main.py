"""The application: one FastAPI app, server-rendered, no build step.

`/healthz` answers from memory alone, so the liveness probe never restarts a
replica because Log Analytics was slow. It reports the image tag, which is
what the shared smoke test checks.
"""

from __future__ import annotations

import logging
import os
import threading
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Depends, FastAPI
from fastapi.staticfiles import StaticFiles

from ..settings import passcode, settings
from . import deps, headers, routes

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    cfg = settings()
    if cfg.require_passcode and not passcode():
        # Fails closed anyway (no passcode matches an empty one), but say so.
        logger.error("APP_PASSCODE is not set: nobody can sign in")
    if not cfg.use_demo:
        # In the background: a slow workspace must not hold up the first probe.
        # The demo has no cache to fill.
        threading.Thread(target=routes.warm, name="cache-warm", daemon=True).start()
    yield


def create_app() -> FastAPI:
    cfg = settings()
    app = FastAPI(
        title="observability",
        lifespan=lifespan,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )
    headers.install(app)

    @app.get("/healthz", include_in_schema=False)
    def healthz() -> dict[str, str]:
        return {
            "status": "ok",
            "environment": cfg.environment,
            "version": os.environ.get("IMAGE_TAG", "unknown"),
        }

    app.include_router(routes.public)
    app.include_router(routes.router, dependencies=[Depends(deps.require_session)])

    app.mount("/static", StaticFiles(directory=routes.STATIC_DIR), name="static")
    vendor = Path(cfg.vendor_dir)
    if vendor.is_dir():
        app.mount("/vendor", StaticFiles(directory=vendor), name="vendor")
    else:
        logger.warning("no %s directory: charts will not draw (make app-vendor)", vendor)
    return app


app = create_app()
