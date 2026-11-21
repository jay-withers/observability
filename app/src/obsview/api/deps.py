"""The passcode gate.

Adapted from health's `api/deps.py` (same cookie scheme, same reasoning):
reachable from the public internet, so the gate defaults on and a missing
passcode fails closed. A shared passcode rather than Entra sign-in, because
this repo has no app registration by decision. The cookie is signed, not
encrypted: it carries only an issue time.
"""

from __future__ import annotations

import hashlib
import hmac
import time

from fastapi import Cookie, HTTPException, status

from ..settings import passcode, settings

COOKIE_NAME = "obsview_session"


def _signing_key() -> bytes:
    """Derived from the passcode when `COOKIE_SECRET` is unset, so a new
    replica does not log anyone out, and rotating the passcode does."""
    configured = settings().cookie_secret
    if configured:
        return configured.encode("utf-8")
    return hashlib.sha256(passcode().encode("utf-8")).digest()


def issue() -> str:
    issued = str(int(time.time()))
    signature = hmac.new(_signing_key(), issued.encode("ascii"), hashlib.sha256).hexdigest()
    return f"{issued}.{signature}"


def valid(token: str | None) -> bool:
    # No passcode means nobody is let in, cookie or not. Without this the
    # signing key falls back to sha256(b""), a public constant, and anyone
    # could forge a session: health's copy has the same hole.
    if not passcode():
        return False
    if not token or "." not in token:
        return False
    issued, _, signature = token.partition(".")
    # isascii as well: isdigit() accepts "²", which encode("ascii") rejects
    # with a 500 rather than a redirect to the login form.
    if not (issued.isascii() and issued.isdigit()):
        return False
    expected = hmac.new(_signing_key(), issued.encode("ascii"), hashlib.sha256).hexdigest()
    if not hmac.compare_digest(signature, expected):
        return False
    return time.time() - int(issued) < settings().cookie_max_age_seconds


def check_passcode(presented: str) -> bool:
    """Compared as bytes: `compare_digest` raises on non-ASCII `str` (gym-log's `£` bug)."""
    expected = passcode()
    return bool(expected) and hmac.compare_digest(
        presented.encode("utf-8"), expected.encode("utf-8")
    )


def require_session(obsview_session: str | None = Cookie(default=None)) -> None:
    """A 303 to the login form rather than a 401, which a browser shows as an error."""
    if not settings().require_passcode:
        return
    if valid(obsview_session):
        return
    raise HTTPException(
        status_code=status.HTTP_303_SEE_OTHER,
        detail="passcode required",
        headers={"Location": "/login"},
    )
