from __future__ import annotations

import hashlib
import hmac
import json
import re
import time
from collections.abc import Iterator
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from obsview import deployments
from obsview.api import deps, routes
from obsview.demo import DemoLogs
from obsview.logs import QueryFailed, Row
from obsview.settings import settings


class Failing:
    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        raise QueryFailed("Log Analytics query failed: HttpResponseError")


class Recording:
    def __init__(self) -> None:
        self.spans: list[timedelta] = []

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        self.spans.append(timespan)
        return []


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


def sign_in(client: TestClient) -> None:
    response = client.post("/login", data={"passcode": "sesame"}, follow_redirects=False)
    assert response.status_code == 303


def use(monkeypatch: pytest.MonkeyPatch, source: object) -> None:
    monkeypatch.setattr(routes, "source", lambda: source)


def test_healthz_is_open_and_reports_the_tag(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("IMAGE_TAG", "v9.9.9")
    body = client.get("/healthz").json()
    assert body["status"] == "ok"
    assert body["version"] == "v9.9.9"


def test_pages_redirect_to_login_without_a_session(client: TestClient) -> None:
    response = client.get("/deployments", follow_redirects=False)
    assert response.status_code == 303
    assert response.headers["location"] == "/login"


def test_wrong_passcode_is_refused(client: TestClient) -> None:
    response = client.post("/login", data={"passcode": "nope"}, follow_redirects=False)
    assert response.status_code == 401
    assert "obsview_session" not in response.cookies


def test_deployments_page_renders_demo_data_with_chart_payload(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/deployments?days=30")
    assert response.status_code == 200
    assert "Demo data" in response.text
    match = re.search(
        r'<script type="application/json" id="chart-data">(.*?)</script>', response.text, re.S
    )
    assert match
    payload = json.loads(match.group(1))
    assert payload["days"] == 30
    assert set(payload["charts"]) >= {"dates", "outcomes", "timeline", "calendar", "lead"}
    assert len(payload["charts"]["dates"]) == 30


def test_root_redirects_to_deployments(client: TestClient) -> None:
    sign_in(client)
    response = client.get("/", follow_redirects=False)
    assert response.headers["location"] == "/deployments"


def test_unknown_range_falls_back_to_the_default(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    recording = Recording()
    use(monkeypatch, recording)
    sign_in(client)
    response = client.get("/deployments?days=12345")
    assert response.status_code == 200
    assert recording.spans == [deployments.window(routes.DEFAULT_RANGE)]
    assert "No deploys in the last 30 days" in response.text
    # No charts to draw, so no chart script either.
    assert "echarts" not in response.text


def test_query_failure_is_a_page_not_a_500(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    response = client.get("/deployments")
    assert response.status_code == 502
    assert "Couldn&#39;t load this view" in response.text or "Couldn't load" in response.text
    assert "HttpResponseError" in response.text


def test_security_headers_are_present(client: TestClient) -> None:
    response = client.get("/login")
    assert "script-src 'self'" in response.headers["content-security-policy"]
    assert response.headers["x-frame-options"] == "DENY"


def test_static_assets_are_served(client: TestClient) -> None:
    assert client.get("/static/app.js").status_code == 200
    assert client.get("/static/app.css").status_code == 200


def test_no_passcode_means_no_session_even_with_a_forged_cookie(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    # With no passcode the key would be sha256(b""), which anyone can compute.
    monkeypatch.setenv("APP_PASSCODE", "")
    monkeypatch.chdir(tmp_path)  # no .env to fall back to
    issued = str(int(time.time()))
    key = hashlib.sha256(b"").digest()
    forged = f"{issued}.{hmac.new(key, issued.encode(), hashlib.sha256).hexdigest()}"
    client.cookies.set("obsview_session", forged)
    response = client.get("/deployments", follow_redirects=False)
    assert response.status_code == 303
    assert response.headers["location"] == "/login"


def test_a_non_ascii_digit_cookie_is_invalid_not_an_error(client: TestClient) -> None:
    # Starlette decodes cookies as latin-1, so "\u00b2" can arrive; the test
    # client cannot send it, so this calls the check the gate makes. "²" passes
    # isdigit(), and used to raise from encode("ascii"), a 500 on every page.
    assert deps.valid("\u00b2.abc") is False


def test_a_malformed_range_falls_back_rather_than_422(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    recording = Recording()
    use(monkeypatch, recording)
    sign_in(client)
    for bad in ("30d", ""):
        assert client.get(f"/deployments?days={bad}").status_code == 200
    assert recording.spans == [deployments.window(routes.DEFAULT_RANGE)] * 2


@pytest.mark.parametrize("path", ["/ops", "/costs"])
def test_ops_and_costs_pages_render_demo_data(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, path: str
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get(f"{path}?days=30")
    assert response.status_code == 200
    assert 'id="chart-data"' in response.text
    assert 'aria-current="page"' in response.text


@pytest.mark.parametrize("path", ["/ops", "/costs"])
def test_ops_and_costs_pages_render_with_no_rows(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, path: str
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert client.get(path).status_code == 200


@pytest.mark.parametrize("path", ["/ops", "/costs"])
def test_ops_and_costs_query_failure_is_a_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, path: str
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    response = client.get(path)
    assert response.status_code == 502
    assert "HttpResponseError" in response.text


class FailedRunsBroken:
    """OpsEvents_CL answers; ContainerAppSystemLogs_CL does not."""

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        if "ContainerAppSystemLogs_CL" in kql:
            raise QueryFailed("Log Analytics query failed: HttpResponseError")
        return DemoLogs(now=datetime.now(UTC)).query(kql, timespan)


def test_ops_page_survives_the_failed_runs_query_failing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, FailedRunsBroken())
    sign_in(client)
    response = client.get("/ops")
    assert response.status_code == 200
    assert "read the failed runs" in response.text
    assert "Jobs and checks" in response.text


def test_readyz_fails_without_a_workspace_unless_demo_is_explicit(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    # The fixture runs with no WORKSPACE_ID: demo data by default, which in
    # Azure means a missing setting, so the smoke test must fail it.
    response = client.get("/readyz")
    assert response.status_code == 503
    assert response.json()["reason"] == "no WORKSPACE_ID"


def test_readyz_reports_a_failing_workspace(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WORKSPACE_ID", "00000000-0000-0000-0000-000000000000")
    monkeypatch.setenv("APP_PASSCODE", "sesame")
    settings.cache_clear()
    from obsview.api.main import create_app

    use(monkeypatch, Failing())
    monkeypatch.setattr(routes, "warm", lambda: None)
    try:
        with TestClient(create_app()) as c:
            response = c.get("/readyz")
    finally:
        settings.cache_clear()
    assert response.status_code == 503
    assert "HttpResponseError" in response.json()["reason"]


def test_readyz_passes_when_the_workspace_answers(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WORKSPACE_ID", "00000000-0000-0000-0000-000000000000")
    monkeypatch.setenv("APP_PASSCODE", "sesame")
    settings.cache_clear()
    from obsview.api.main import create_app

    recording = Recording()
    use(monkeypatch, recording)
    monkeypatch.setattr(routes, "warm", lambda: None)
    try:
        with TestClient(create_app()) as c:
            response = c.get("/readyz")
    finally:
        settings.cache_clear()
    assert response.status_code == 200
    assert recording.spans == [routes.ready_query()[1]]


def test_readyz_passes_on_explicit_demo(monkeypatch: pytest.MonkeyPatch) -> None:
    # DEMO=true with no workspace is a deliberate demo, not a misconfiguration.
    monkeypatch.setenv("WORKSPACE_ID", "")
    monkeypatch.setenv("DEMO", "true")
    monkeypatch.setenv("APP_PASSCODE", "sesame")
    settings.cache_clear()
    routes._build_source.cache_clear()
    from obsview.api.main import create_app

    try:
        with TestClient(create_app()) as c:
            response = c.get("/readyz")
    finally:
        settings.cache_clear()
        routes._build_source.cache_clear()
    assert response.status_code == 200


# --- per-app pages ---------------------------------------------------------------


@pytest.mark.parametrize("path", ["/apps", "/apps/gym-log"])
def test_app_pages_need_a_session(client: TestClient, path: str) -> None:
    response = client.get(path, follow_redirects=False)
    assert response.status_code in (303, 307)


def test_apps_index_lists_every_app_with_demo_data(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    from obsview import apps

    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/apps?days=7")
    assert response.status_code == 200
    for a in apps.APPS:
        assert f'href="/apps/{a.slug}?days=7"' in response.text
    assert "error line" in response.text


@pytest.mark.parametrize("slug", ["gym-log", "finances", "market-agent", "career"])
def test_app_page_renders_every_section_with_demo_data(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, slug: str
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get(f"/apps/{slug}?days=30")
    assert response.status_code == 200
    for section in ('id="errors"', 'id="deployments"', 'id="ops"', 'id="costs"'):
        assert section in response.text
    payload = json.loads(
        re.search(r'id="chart-data">(.*?)</script>', response.text, re.S).group(1)  # type: ignore[union-attr]
    )
    assert set(payload["groups"]) == {"deploy", "ops", "costs"}
    assert 'data-group="deploy" data-chart="outcomes"' in response.text
    # Estate-only cards stay on the estate views.
    assert "Session resources running" not in response.text
    assert "Forecast to month end" not in response.text


def test_app_page_shows_only_that_apps_rows(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    text = client.get("/apps/gym-log").text
    assert "caj-gymlog-dev-backup" in text
    assert "caj-finances-dev-backup" not in text
    assert "rg-platform-dev" not in text
    assert "GET /sessions/N → 500" in text


def test_unknown_app_is_404(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert client.get("/apps/nope").status_code == 404


def test_app_pages_render_with_no_rows(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert client.get("/apps").status_code == 200
    response = client.get("/apps/career?days=1")
    assert response.status_code == 200
    assert "No errors logged in the last 1 day." in response.text


class OnlyDeploysAnswer:
    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        if kql.startswith("Deployments_CL"):
            return DemoLogs(now=datetime.now(UTC)).query(kql, timespan)
        raise QueryFailed("Log Analytics query failed: HttpResponseError")


def test_app_pages_render_around_failed_secondary_queries(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, OnlyDeploysAnswer())
    sign_in(client)
    response = client.get("/apps/gym-log")
    assert response.status_code == 200
    assert "Couldn't read the console logs" in response.text
    assert "Couldn't read the checks" in response.text
    assert "Couldn't read the costs" in response.text
    index = client.get("/apps")
    assert index.status_code == 200
    assert "spend unavailable" in index.text


def test_app_page_deploy_failure_is_a_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    assert client.get("/apps/gym-log").status_code == 502
    assert client.get("/apps").status_code == 502


@pytest.mark.parametrize("days", [1, 3, 5, 7, 30, 60, 90])
def test_every_range_is_offered_and_honoured(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, days: int
) -> None:
    recording = Recording()
    use(monkeypatch, recording)
    sign_in(client)
    response = client.get(f"/deployments?days={days}")
    assert recording.spans == [deployments.window(days)]
    for r in routes.RANGES:
        assert f'href="?days={r}"' in response.text


def test_deployment_app_cards_link_to_the_app_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    assert 'href="/apps/gym-log?days=30"' in client.get("/deployments").text


class FailedRunsOnlyBroken:
    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        if "ContainerAppSystemLogs_CL" in kql:
            raise QueryFailed("Log Analytics query failed: HttpResponseError")
        return []


def test_app_page_with_no_checks_does_not_hide_a_failed_runs_failure(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, FailedRunsOnlyBroken())
    sign_in(client)
    text = client.get("/apps/career").text
    assert "No checks or failed runs" not in text
    assert "read the failed runs" in text


def test_apps_index_does_not_run_the_failed_runs_query(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    seen: list[str] = []

    class Spy:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            seen.append(kql)
            return []

    use(monkeypatch, Spy())
    sign_in(client)
    assert client.get("/apps").status_code == 200
    assert not any("ContainerAppSystemLogs_CL" in k for k in seen)
