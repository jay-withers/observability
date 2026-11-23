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
from typing import Any

from fastapi import Depends, FastAPI, Response, status
from fastapi.staticfiles import StaticFiles

from ..logs import QueryFailed
from ..settings import passcode, settings
from . import csrf, deps, headers, routes

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
    # Before headers, so a cross-site refusal carries the security headers too.
    csrf.install(app)
    headers.install(app)

    @app.get("/healthz", include_in_schema=False)
    def healthz() -> dict[str, str]:
        return {
            "status": "ok",
            "environment": cfg.environment,
            "version": os.environ.get("IMAGE_TAG", "unknown"),
        }

    @app.get("/readyz", include_in_schema=False)
    def readyz(response: Response) -> dict[str, Any]:
        """Can this replica read the workspace? The shared smoke test checks it
        after every deploy, so a wrong WORKSPACE_ID or a missing role fails the
        deploy (and rolls it back) instead of the first page load.

        Not the readiness probe: a slow workspace must not take the app out of
        rotation, since its error page is more use than no page at all.
        """
        if cfg.use_demo and not cfg.demo:
            # Demo data in Azure is a misconfiguration, not a feature.
            response.status_code = status.HTTP_503_SERVICE_UNAVAILABLE
            return {"status": "unavailable", "workspace": False, "reason": "no WORKSPACE_ID"}
        try:
            routes.source().query(*routes.ready_query())
        except QueryFailed as exc:
            response.status_code = status.HTTP_503_SERVICE_UNAVAILABLE
            return {"status": "unavailable", "workspace": False, "reason": str(exc)}
        return {"status": "ok", "workspace": True}

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
