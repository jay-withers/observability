"""One entrypoint: `obsview serve`."""

from __future__ import annotations

import argparse
import logging

from .settings import settings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="obsview")
    sub = parser.add_subparsers(dest="command", required=True)
    serve = sub.add_parser("serve", help="run the web application")
    serve.add_argument("--host", default="0.0.0.0")  # noqa: S104
    serve.add_argument("--port", type=int, default=8000)
    serve.add_argument("--reload", action="store_true", help="reload on edit, for local work")
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=settings().log_level.upper(),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    if args.command == "serve":
        import uvicorn

        uvicorn.run(
            "obsview.api.main:app",
            host=args.host,
            port=args.port,
            reload=args.reload,
            proxy_headers=True,
            forwarded_allow_ips="*",
        )
    return 0
