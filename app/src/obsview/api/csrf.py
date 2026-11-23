"""Cross-site request forgery defences: an origin check and a form token.

Two layers, because they answer different things:

**The origin check (`install`) covers every form and fetch.** Every unsafe
request (POST, PUT, PATCH, DELETE) a browser sends cross-site is refused
with a 403, so a page behind the passcode needs no token of its own. Browsers
say where a request came from in `Sec-Fetch-Site`, which a page cannot forge
or suppress. A browser too old to send it still sends `Origin` on a
cross-origin POST, and that is compared with `Host` (host and port only: TLS
ends at the ingress, so the app can't see the scheme the browser used, and
HSTS keeps browsers off http). A request with neither passes: curl, a
webhook, a test client, which carry no ambient cookies worth forging. A
browser old enough to send neither on a form POST, or one whose extension
strips both, falls back on the session cookie's `SameSite=Lax`. "same-site"
is refused too: every app is its own subdomain of one site, and none should
post into another. That also refuses a POST redirected through a sibling
subdomain, which no app's flow does (Cloudflare Access is another site, and
returns with a GET).

**The form token (`token`, `valid`) is double-submit.** `token(request)`
gives a page the value of the `__Host-csrf` cookie, minting one if the browser
has none, and the middleware sets the cookie on that response only, so a page
without a form sets nothing. The form posts it back as `csrf_token`, and
`valid` checks the field matches the cookie. A forger can make a browser send
the cookie but can't read it to fill in the field. It's on the login form,
where `SameSite=Lax` session cookies prove nothing because there is no
session yet, and it is the field DAST scanners (ZAP's "Absence of Anti-CSRF
Tokens") look for. `__Host-` pins the cookie to this exact origin, over
HTTPS, at `/`: no sibling subdomain can set it.

A refused token should re-render the form with `token(request)`, so a retry
works: a browser with no cookie that opens the login form in two tabs at once
gets two tokens, and only the later tab's matches.

The cookie is `Secure`, like the session cookie. Chromium and Firefox treat
localhost as a secure origin, so a local `make run` over http works there;
httpx does not, so a test client needs `base_url="https://testserver"`.
"""

from __future__ import annotations

import hmac
import re
import secrets
from collections.abc import Awaitable, Callable
from urllib.parse import urlsplit

from fastapi import FastAPI, Request, Response
from fastapi.responses import PlainTextResponse

COOKIE_NAME = "__Host-csrf"
# The form field. ZAP recognises this name as an anti-CSRF token.
FIELD = "csrf_token"
# A long-lived cookie: it is a random value, not a credential, and a login
# page left open for a week should still submit.
COOKIE_MAX_AGE_SECONDS = 30 * 24 * 3600

UNSAFE_METHODS = frozenset({"POST", "PUT", "PATCH", "DELETE"})
# `none` is a navigation the user started (typed, bookmarked); it carries no
# forged intent.
TRUSTED_SITES = frozenset({"same-origin", "none"})

# What `token` mints: secrets.token_urlsafe(32), 43 URL-safe characters. A cookie
# that doesn't look like one is replaced, so a junk value is never echoed into
# a page.
_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}")
_FIELD_TAG = re.compile(rf"""<input\b[^>]*\bname=["']{FIELD}["'][^>]*>""")
_VALUE = re.compile(r"""\bvalue=["']([^"']*)["']""")
_DEFAULT_PORTS = {"http": "80", "https": "443"}

_NEW = "csrf_new_token"


def cross_site(request: Request) -> bool:
    """Whether a browser sent this request from another origin."""
    site = request.headers.get("sec-fetch-site")
    if site is not None:
        return site.lower() not in TRUSTED_SITES
    origin = request.headers.get("origin")
    if origin is None:
        return False
    # "null" is an opaque origin: a sandboxed frame, a data: URL, a file.
    if origin == "null":
        return True
    parts = urlsplit(origin)
    return _host(parts.netloc, parts.scheme) != _host(request.headers.get("host", ""), parts.scheme)


def _host(netloc: str, scheme: str) -> str:
    """`netloc` lowercased, without the scheme's default port, which a proxy
    may or may not have spelled out in `Host`."""
    netloc = netloc.lower()
    default = _DEFAULT_PORTS.get(scheme.lower())
    if default and netloc.endswith(f":{default}"):
        return netloc[: -len(default) - 1]
    return netloc


def token(request: Request) -> str:
    """The form token for this browser: its cookie's, or a new one the
    middleware sets on this response. For templates, as a Jinja global:
    `<input type="hidden" name="csrf_token" value="{{ csrf_token(request) }}">`.
    """
    minted: str | None = getattr(request.state, _NEW, None)
    if minted is not None:
        return minted
    existing = request.cookies.get(COOKIE_NAME, "")
    if _TOKEN.fullmatch(existing):
        return existing
    minted = secrets.token_urlsafe(32)
    setattr(request.state, _NEW, minted)
    return minted


def valid(request: Request, presented: str) -> bool:
    """Whether the form's `csrf_token` matches this browser's cookie.

    Compared as bytes, constant-time: `compare_digest` raises on a `str`
    holding anything outside ASCII, and both values are whatever the client
    sent.
    """
    expected = request.cookies.get(COOKIE_NAME, "")
    return bool(expected) and hmac.compare_digest(
        presented.encode("utf-8"), expected.encode("utf-8")
    )


def token_in(html: str) -> str:
    """The `csrf_token` field's value in a rendered page, for an app's tests,
    which post forms through a test client rather than a browser."""
    tag = _FIELD_TAG.search(html)
    value = _VALUE.search(tag.group(0)) if tag else None
    if value is None:
        raise ValueError(f"no {FIELD} field in the page")
    return value.group(1)


def install(app: FastAPI) -> None:
    """Refuse cross-site unsafe requests, and set the token cookie when a
    page used `token`.

    Install it before `headers.install`, so the headers middleware wraps this
    one and a refusal carries the security headers too.
    """

    @app.middleware("http")
    async def _csrf(
        request: Request, call_next: Callable[[Request], Awaitable[Response]]
    ) -> Response:
        if request.method in UNSAFE_METHODS and cross_site(request):
            return PlainTextResponse("Cross-site request refused", status_code=403)
        response = await call_next(request)
        minted = getattr(request.state, _NEW, None)
        if minted is not None:
            response.set_cookie(
                COOKIE_NAME,
                minted,
                max_age=COOKIE_MAX_AGE_SECONDS,
                path="/",
                secure=True,
                httponly=True,
                samesite="lax",
            )
        return response
