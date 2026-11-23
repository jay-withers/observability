from __future__ import annotations

import hashlib
import hmac
import json
import re
import time
from collections.abc import Iterator
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

from obsview import deployments
from obsview.api import csrf, deps, routes
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


def login_form(client: TestClient, passcode: str) -> dict[str, str]:
    """What the login form posts: the passcode and the page's CSRF token."""
    return {"passcode": passcode, "csrf_token": csrf.token_in(client.get("/login").text)}


def sign_in(client: TestClient) -> None:
    response = client.post("/login", data=login_form(client, "sesame"), follow_redirects=False)
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
    response = client.get("/apps", follow_redirects=False)
    assert response.status_code == 303
    assert response.headers["location"] == "/login"


def test_wrong_passcode_is_refused(client: TestClient) -> None:
    response = client.post("/login", data=login_form(client, "nope"), follow_redirects=False)
    assert response.status_code == 401
    assert "obsview_session" not in response.cookies


def test_the_login_form_carries_a_csrf_token(client: TestClient) -> None:
    page = client.get("/login")
    assert page.cookies.get(csrf.COOKIE_NAME) == csrf.token_in(page.text)


def test_a_login_without_the_csrf_token_is_refused_even_with_the_passcode(
    client: TestClient,
) -> None:
    client.get("/login")
    response = client.post("/login", data={"passcode": "sesame"}, follow_redirects=False)
    assert response.status_code == 403
    assert "expired" in response.text
    assert "obsview_session" not in response.cookies
    # The re-render carries a token that works, so a retry signs in.
    retry = {"passcode": "sesame", "csrf_token": csrf.token_in(response.text)}
    assert client.post("/login", data=retry, follow_redirects=False).status_code == 303


def test_a_browser_that_keeps_no_cookie_is_told_cookies_are_needed(
    client: TestClient,
) -> None:
    token = csrf.token_in(client.get("/login").text)
    client.cookies.clear()
    response = client.post(
        "/login", data={"passcode": "sesame", "csrf_token": token}, follow_redirects=False
    )
    assert response.status_code == 403
    assert "needs cookies" in response.text
    assert "expired" not in response.text


def test_a_cross_site_login_is_refused(client: TestClient) -> None:
    response = client.post(
        "/login",
        data=login_form(client, "sesame"),
        headers={"Sec-Fetch-Site": "cross-site"},
        follow_redirects=False,
    )
    assert response.status_code == 403
    assert "content-security-policy" in response.headers


def test_apps_index_renders_all_apps_deploys_with_chart_payload(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/apps?days=30")
    assert response.status_code == 200
    assert "Demo data" in response.text
    match = re.search(
        r'<script type="application/json" id="chart-data">(.*?)</script>', response.text, re.S
    )
    assert match
    payload = json.loads(match.group(1))
    assert payload["days"] == 30
    charts = payload["groups"]["deploy"]
    assert set(charts) >= {"dates", "outcomes", "timeline", "calendar", "lead"}
    assert len(charts["dates"]) == 30
    assert 'data-group="deploy" data-chart="outcomes"' in response.text


def test_home_is_a_tile_per_page(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/?days=30")
    assert response.status_code == 200
    tiles = re.findall(r'<a class="card nav-tile" href="([^"]+)"', response.text)
    assert tiles == ["/apps?days=30", "/slo?days=30", "/platform?days=30", "/overall?days=30"]
    assert "spent in" in response.text
    assert "unavailable" not in response.text


def test_home_needs_a_session(client: TestClient) -> None:
    response = client.get("/", follow_redirects=False)
    assert response.status_code in (303, 307)
    assert response.headers["location"].startswith("/login")


def test_the_brand_opens_home_at_the_same_range(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert '<a class="brand" href="/?days=30">' in client.get("/apps?days=30").text


def test_home_splits_problems_and_errors_between_apps_and_the_platform(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = datetime.now(UTC)
    ops_rows = [
        {"TimeGenerated": now, "Kind": k, "Name": n, "Status": s, "Detail": ""}
        for k, n, s in (
            ("heartbeat", "caj-gymlog-dev-backup", "overdue"),  # health's
            ("drift", "finances/terraform", "drifted"),  # finances'
            ("drift", "azure-landingzone/hub", "drifted"),  # the platform's
            ("session", "vm", "ok"),
            ("cost-feed", "Costs_CL", "ok"),
        )
    ]
    deploy_rows = [
        {"TimeGenerated": now, "Repo": r, "Outcome": "success"}
        # The last is a repo no longer in apps.APPS: on no app's tile, so not in the sum.
        for r in ("health", "finances", "retired-app")
    ]
    error_rows = [
        {"Workload": "ca-gymlog-dev", "Total": 4, "TimeGenerated": now, "Log_s": "ERROR x"},
        {"Workload": "ca-platform-thing", "Total": 3, "TimeGenerated": now, "Log_s": "ERROR y"},
    ]

    class Rows:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            first = kql.splitlines()[0]
            if "OpsEvents_CL" in first:
                return ops_rows
            if "ContainerAppConsoleLogs" in first:
                return error_rows
            if "Deployments_CL" in first:
                return deploy_rows
            return []

    use(monkeypatch, Rows())
    sign_in(client)
    text = client.get("/").text
    apps_tile, _, platform_tile, _ = re.findall(r'<a class="card nav-tile".*?</a>', text, re.S)
    assert "2 problems" in apps_tile
    assert '<div class="value">2</div>' in apps_tile
    assert "4 error lines" in apps_tile
    assert '<div class="value">1</div>' in platform_tile
    # The big number is the count; the badge doesn't say it again.
    assert "needs attention" in platform_tile
    assert "problem</span>" not in platform_tile
    assert "3 error lines" in platform_tile


def test_home_counts_a_quiet_always_reporting_check_as_a_platform_problem(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    text = client.get("/").text
    platform_tile = re.findall(r'<a class="card nav-tile".*?</a>', text, re.S)[2]
    # Session resources and the cost feed, as on /platform.
    assert '<div class="value">2</div>' in platform_tile


def test_home_with_no_budget_says_so_rather_than_over_budget(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("BUDGET", "0")
    settings.cache_clear()
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    overall_tile = re.findall(r'<a class="card nav-tile".*?</a>', client.get("/").text, re.S)[3]
    assert "budget</span>" not in overall_tile
    assert "No budget set" in overall_tile


def test_home_renders_around_every_query_failing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    response = client.get("/")
    assert response.status_code == 200
    for what in ("Deploys", "Checks", "Costs", "SLOs"):
        assert f"{what} unavailable" in response.text
    # Regression: the SLO tile said "partly unavailable" over "SLOs unavailable".
    assert "partly unavailable" not in response.text
    assert len(re.findall(r'class="card nav-tile"', response.text)) == 4


def test_home_reads_the_warmed_queries(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    seen: list[tuple[str, timedelta]] = []

    class Spy:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            seen.append((kql, timespan))
            return []

    use(monkeypatch, Spy())
    sign_in(client)
    assert client.get("/").status_code == 200
    warmed = routes._home_queries(routes.DEFAULT_RANGE, datetime.now(UTC))
    assert sorted(k for k, _ in seen) == sorted(k for k, _ in warmed.values())


@pytest.mark.parametrize(
    ("old", "new"),
    [
        ("/deployments?days=7", "/apps?days=7"),
        ("/ops?days=90", "/platform?days=90"),
        ("/costs", "/overall?days=7"),
        ("/costs?days=junk", "/overall?days=7"),
    ],
)
def test_the_old_views_redirect(client: TestClient, old: str, new: str) -> None:
    sign_in(client)
    response = client.get(old, follow_redirects=False)
    assert response.status_code == 303
    assert response.headers["location"] == new


def test_the_nav_is_apps_slos_platform_and_overall(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    nav = re.search(r'<nav class="tabs".*?</nav>', client.get("/apps").text, re.S)
    assert nav
    assert re.findall(r'href="(/[a-z]+)\?', nav.group(0)) == [
        "/apps",
        "/slo",
        "/platform",
        "/overall",
    ]


def test_unknown_range_falls_back_to_the_default(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    recording = Recording()
    use(monkeypatch, recording)
    sign_in(client)
    response = client.get("/apps?days=12345")
    assert response.status_code == 200
    assert deployments.window(routes.DEFAULT_RANGE) in recording.spans
    assert "No deploys in the last 7 days" in response.text
    # No charts to draw, so no chart script either.
    assert "echarts" not in response.text


def test_query_failure_is_a_page_not_a_500(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    response = client.get("/apps")
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
    response = client.get("/apps", follow_redirects=False)
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
        assert client.get(f"/apps?days={bad}").status_code == 200
        assert deployments.window(routes.DEFAULT_RANGE) in recording.spans
        recording.spans.clear()


def test_platform_renders_demo_data(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/platform?days=30")
    assert response.status_code == 200
    payload = json.loads(
        re.search(r'id="chart-data">(.*?)</script>', response.text, re.S).group(1)  # type: ignore[union-attr]
    )
    assert set(payload["groups"]) == {"ops", "costs"}
    assert 'aria-current="page"' in response.text
    for section in ('id="ops"', 'id="costs"', 'id="errors"'):
        assert section in response.text


def test_platform_shows_only_what_no_app_owns(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    text = client.get("/platform").text
    assert "azure-landingzone/hub" in text
    assert "Session resources" in text
    assert "rg-platform-dev" in text
    assert "health/terraform" not in text
    assert "caj-gymlog-dev-backup" not in text
    assert "rg-finances-dev" not in text
    # The budget is the subscription's: its card is on Overall only.
    assert "Forecast to month end" not in text
    assert "Forecast to month end" not in client.get("/apps/health").text
    assert "Forecast to month end" in client.get("/overall").text


def two_groups(monkeypatch: pytest.MonkeyPatch) -> None:
    now = datetime.now(UTC)
    rows = [
        {"UsageDate": now, "ResourceGroup": rg, "ServiceName": svc, "Cost": cost, "Currency": "USD"}
        for rg, svc, cost in (
            ("rg-platform-dev", "Log Analytics", 3.0),
            ("rg-gymlog-dev", "Gym compute", 4.0),
        )
    ]

    class Costs:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            return rows if "Costs_CL" in kql.split("\n", 1)[0] else []

    use(monkeypatch, Costs())


def chart_payload(text: str) -> Any:
    found = re.search(r'id="chart-data">(.*?)</script>', text, re.S)
    assert found
    return json.loads(found.group(1))


@pytest.mark.parametrize(
    ("path", "own", "other"),
    [
        ("/platform?days=1", ("Log Analytics", "$3.00"), ("Gym compute", "$7.00", "$4.00")),
        ("/apps/health?days=1", ("Gym compute", "$4.00"), ("Log Analytics", "$7.00", "$3.00")),
    ],
)
def test_a_slices_costs_show_only_its_own_spend(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
    own: tuple[str, ...],
    other: tuple[str, ...],
) -> None:
    two_groups(monkeypatch)
    sign_in(client)
    text = client.get(path).text
    for s in own:
        assert s in text
    for s in other:
        assert s not in text
    charts = chart_payload(text)["groups"]["costs"]
    # Stacked by the slice's own services, never the estate's areas.
    assert charts["series_keys"] == [own[0]]
    assert "platform" not in charts["series_keys"] and "apps" not in charts["series_keys"]


def test_overall_breaks_down_every_resource_group(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    two_groups(monkeypatch)
    sign_in(client)
    response = client.get("/overall?days=1")
    assert response.status_code == 200
    text = response.text
    assert '<div class="hero-number">$7.00</div>' in text
    assert '<div class="value">$7.00</div>' in text
    for s in ("By area", "rg-platform-dev", "rg-gymlog-dev", "Log Analytics", "Gym compute"):
        assert s in text
    charts = chart_payload(text)["groups"]["costs"]
    assert charts["series_keys"][:2] == ["platform", "apps"]
    assert [n["name"] for n in charts["treemap"]] == ["platform", "apps"]


def test_overall_renders_demo_data(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/overall?days=30")
    assert response.status_code == 200
    assert 'data-group="costs" data-chart="treemap"' in response.text


def test_overall_query_failure_is_a_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    assert client.get("/overall").status_code == 502


def test_overall_needs_a_session(client: TestClient) -> None:
    assert client.get("/overall", follow_redirects=False).status_code in (303, 307)


def test_platform_renders_with_no_rows(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    response = client.get("/platform")
    assert response.status_code == 200
    assert "echarts" not in response.text or 'id="chart-data"' in response.text


def test_platform_says_when_an_always_reporting_check_goes_quiet(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    text = client.get("/platform").text
    assert "2 problems now" in text
    assert "Session resources <small>no row</small>" in text
    assert "Cost feed <small>no row</small>" in text
    # An app page has no such checks, so it says nothing about them.
    assert "no row" not in client.get("/apps/health").text


def test_platform_with_both_checks_reporting_is_all_clear(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = datetime.now(UTC)
    rows = [
        {"TimeGenerated": now, "Kind": k, "Name": n, "Status": "ok", "Detail": ""}
        for k, n in (("session", "vm"), ("cost-feed", "Costs_CL"))
    ]

    class Ops:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            return rows if "OpsEvents_CL" in kql.splitlines()[0] else []

    use(monkeypatch, Ops())
    sign_in(client)
    text = client.get("/platform").text
    assert "All clear" in text
    assert "no row" not in text


def test_warm_up_fills_the_default_pages_queries(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: list[tuple[str, timedelta]] = []

    class Spy:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            seen.append((kql, timespan))
            return []

    use(monkeypatch, Spy())
    routes.warm()
    assert routes.ready_query() in seen
    assert len(seen) == len(routes._home_queries(routes.DEFAULT_RANGE, datetime.now(UTC)))


def test_platform_renders_around_every_query_failing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    response = client.get("/platform")
    assert response.status_code == 200
    assert "Couldn't read the checks" in response.text
    assert "Couldn't read the costs" in response.text
    assert "Couldn't read the console logs" in response.text
    assert "echarts" not in response.text


class FailedRunsBroken:
    """OpsEvents_CL answers; ContainerAppSystemLogs_CL does not."""

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        if "ContainerAppSystemLogs_CL" in kql:
            raise QueryFailed("Log Analytics query failed: HttpResponseError")
        return DemoLogs(now=datetime.now(UTC)).query(kql, timespan)


def test_platform_survives_the_failed_runs_query_failing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, FailedRunsBroken())
    sign_in(client)
    response = client.get("/platform")
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


# --- apps and the platform ---------------------------------------------------------------


@pytest.mark.parametrize("path", ["/apps", "/apps/health"])
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


@pytest.mark.parametrize("slug", ["health", "finances", "market-agent", "career"])
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
    text = client.get("/apps/health").text
    assert "caj-gymlog-dev-backup" in text
    assert "caj-finances-dev-backup" not in text
    assert "rg-platform-dev" not in text
    assert "GET /sessions/N → 500" in text


@pytest.mark.parametrize("page", ["apps", "slo"])
def test_an_apps_former_name_redirects_to_its_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, page: str
) -> None:
    """A bookmark from before health was renamed from gym-log."""
    use(monkeypatch, Recording())
    sign_in(client)
    response = client.get(f"/{page}/gym-log?days=30", follow_redirects=False)
    assert response.status_code == 303
    assert response.headers["location"] == f"/{page}/health?days=30"


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
    response = client.get("/apps/health")
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
    assert client.get("/apps/health").status_code == 502
    assert client.get("/apps").status_code == 502


@pytest.mark.parametrize("days", [1, 3, 5, 7, 30, 60, 90])
def test_every_range_is_offered_and_honoured(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, days: int
) -> None:
    recording = Recording()
    use(monkeypatch, recording)
    sign_in(client)
    response = client.get(f"/apps?days={days}")
    assert deployments.window(days) in recording.spans
    for r in routes.RANGES:
        assert f'href="?days={r}"' in response.text


def test_deployment_app_cards_link_to_the_app_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    text = client.get("/apps").text
    deploys = text[text.index('id="deployments"') :]
    assert 'href="/apps/health?days=7"' in deploys


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


def test_the_default_range_is_seven_days(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    text = client.get("/apps").text
    assert re.search(r'<a href="\?days=7" aria-current="true">7d</a>', text)


@pytest.mark.parametrize("path", ["/apps/health", "/platform"])
def test_section_links_are_on_the_page_they_jump_within(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, path: str
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    text = client.get(path).text
    nav = re.search(r'<nav class="jump".*?</nav>', text, re.S)
    assert nav
    for target in re.findall(r'href="#([a-z]+)"', nav.group(0)):
        assert f'id="{target}"' in text


# --- SLOs -----------------------------------------------------------------------


@pytest.mark.parametrize("path", ["/slo", "/slo/health"])
def test_slo_pages_need_a_session(client: TestClient, path: str) -> None:
    response = client.get(path, follow_redirects=False)
    assert response.status_code in (303, 307)


def test_slo_index_is_a_tile_per_app_linking_to_its_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    from obsview import apps

    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get("/slo?days=30")
    assert response.status_code == 200
    tiles = re.findall(r'<a class="card nav-tile" href="([^"]+)"', response.text)
    assert tiles == [f"/slo/{a.slug}?days=30" for a in apps.APPS]
    assert "budget left" in response.text
    # The window is fixed, so no range to pick.
    assert 'class="range"' not in response.text


def test_slo_index_leaves_request_slis_off_job_only_apps(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    text = client.get("/slo").text
    tile = re.search(r'href="/slo/repo-agent.*?</a>', text, re.S)
    assert tile
    assert "Job runs" in tile.group(0)
    assert "Availability" not in tile.group(0)


@pytest.mark.parametrize("slug", ["health", "repo-agent", "observability"])
def test_slo_app_page_renders_each_sli_with_a_burndown(
    client: TestClient, monkeypatch: pytest.MonkeyPatch, slug: str
) -> None:
    from obsview import slo

    use(monkeypatch, DemoLogs(now=datetime.now(UTC)))
    sign_in(client)
    response = client.get(f"/slo/{slug}?days=7")
    assert response.status_code == 200
    assert f'href="/apps/{slug}?days=7"' in response.text
    payload = json.loads(
        re.search(r'id="chart-data">(.*?)</script>', response.text, re.S).group(1)  # type: ignore[union-attr]
    )
    burndown = payload["groups"]["slo"]["burndown"]
    assert burndown
    for key, chart in burndown.items():
        assert f'data-key="{key}"' in response.text
        assert len(chart["points"]) == slo.WINDOW_DAYS


def test_slo_app_page_says_when_an_sli_has_no_events(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    response = client.get("/slo/health")
    assert response.status_code == 200
    assert "No requests in the last 28 days" in response.text
    assert "No runs in the last 28 days" in response.text
    # Nothing to draw, so no charts loaded.
    assert "echarts" not in response.text


def test_slo_pages_render_around_every_query_failing(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Failing())
    sign_in(client)
    index = client.get("/slo")
    assert index.status_code == 200
    assert "SLIs unavailable" in index.text
    page = client.get("/slo/finances")
    assert page.status_code == 200
    assert page.text.count("Couldn't read this SLI") == 4


def test_unknown_app_slo_page_is_404(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert client.get("/slo/nope").status_code == 404


def test_slo_pages_share_the_deploy_query_with_the_other_pages(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    from obsview import slo

    seen: list[tuple[str, timedelta]] = []

    class Spy:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            seen.append((kql, timespan))
            return []

    use(monkeypatch, Spy())
    sign_in(client)
    client.get("/slo?days=30")
    assert (deployments.kql(settings().environment), deployments.window(30)) in seen
    assert (slo.HTTP_KQL, slo.WINDOW) in seen
    assert (slo.JOBS_KQL, slo.WINDOW) in seen


def test_app_page_links_to_its_slo_page(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    assert 'href="/slo/career?days=7"' in client.get("/apps/career").text


def test_home_slo_tile_counts_apps_within_budget(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = datetime.now(UTC)

    class OnlyDeploys:
        def query(self, kql: str, timespan: timedelta) -> list[Row]:
            if not kql.startswith("Deployments_CL"):
                return []
            # health: every deploy fails (budget spent); finances: all good.
            return [
                {
                    "TimeGenerated": now - timedelta(hours=h),
                    "Repo": repo,
                    "App": repo,
                    "Outcome": outcome,
                    "Trigger": "merge",
                }
                for h in range(1, 4)
                for repo, outcome in (("health", "failed"), ("finances", "success"))
            ]

    use(monkeypatch, OnlyDeploys())
    sign_in(client)
    text = client.get("/").text
    tile = re.search(r'<a class="card nav-tile" href="/slo\?days=7".*?</a>', text, re.S)
    assert tile
    assert "1 spent" in tile.group(0)
    assert re.search(r'<div class="value">1<small class="of"> of 2</small>', tile.group(0))


def test_home_slo_tile_with_no_events_says_so_not_0_of_0(
    client: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    use(monkeypatch, Recording())
    sign_in(client)
    text = client.get("/").text
    tile = re.search(r'<a class="card nav-tile" href="/slo\?days=7".*?</a>', text, re.S)
    assert tile
    assert "No SLO events in 28 days" in tile.group(0)
    assert " of 0" not in tile.group(0)
