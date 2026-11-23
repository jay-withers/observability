"""The copy of appkit's csrf.py, through obsview's own login route.

appkit's suite covers the module in full; these pin the parts the route tests
don't reach: the `Origin` fallback for a browser without `Sec-Fetch-Site`,
and `token_in` on a page with no field.
"""

from __future__ import annotations

from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient

from obsview.api import csrf, routes
from obsview.settings import settings


@pytest.fixture
def client(monkeypatch: pytest.MonkeyPatch) -> Iterator[TestClient]:
    monkeypatch.setenv("APP_PASSCODE", "sesame")
    monkeypatch.setenv("WORKSPACE_ID", "")
    monkeypatch.delenv("COOKIE_SECRET", raising=False)
    settings.cache_clear()
    routes._build_source.cache_clear()
    from obsview.api.main import create_app

    with TestClient(create_app(), base_url="https://testserver") as c:
        yield c
    settings.cache_clear()
    routes._build_source.cache_clear()


@pytest.mark.parametrize(
    ("origin", "status"),
    [
        ("https://testserver", 303),
        ("https://testserver:443", 303),
        ("https://evil.example", 403),
        ("null", 403),
    ],
)
def test_without_sec_fetch_site_the_origin_decides(
    client: TestClient, origin: str, status: int
) -> None:
    token = csrf.token_in(client.get("/login").text)
    response = client.post(
        "/login",
        data={"passcode": "sesame", "csrf_token": token},
        headers={"Origin": origin},
        follow_redirects=False,
    )
    assert response.status_code == status


def test_token_in_says_when_the_page_has_no_field() -> None:
    with pytest.raises(ValueError, match="csrf_token"):
        csrf.token_in("<p>no form</p>")
