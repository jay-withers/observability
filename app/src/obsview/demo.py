"""Generated rows, for running the app with no Azure at all.

`make app-run` without a `WORKSPACE_ID` serves these, under a "demo data"
banner. They follow the real tables' columns and roughly their shape (a few
deploys a day, mostly Renovate, the odd rollback), seeded so a screenshot is
the same twice.
"""

from __future__ import annotations

import random
from datetime import UTC, datetime, timedelta

from .logs import Row

APPS = [
    ("gym-log", "gymlog"),
    ("finances", "finances"),
    ("career", "career"),
    ("observability", "obsview"),
    ("repo-agent", "repoagent"),
    ("security-testing", "scanner"),
]


class DemoLogs:
    def __init__(self, now: datetime | None = None, seed: int = 7) -> None:
        self._now = now
        self._seed = seed

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        now = self._now or datetime.now(UTC)
        table = kql.lstrip().split("\n", 1)[0].split(" ", 1)[0]
        if table == "OpsEvents_CL":
            return _ops(now, timespan, random.Random(self._seed))
        if table == "ContainerAppSystemLogs_CL":
            return _failed_runs(now)
        if table == "Costs_CL":
            return _costs(now, timespan, random.Random(self._seed))
        if table != "Deployments_CL":
            return []
        rng = random.Random(self._seed)
        rows: list[Row] = []
        versions = {app: [0, rng.randint(3, 20), 0] for _, app in APPS}
        # Generated from a fixed origin forwards, then cut to the window, so a
        # shorter range is a suffix of a longer one, as with real data.
        start = now - timedelta(days=365)
        day = start
        while day < now:
            for repo, app in APPS:
                if rng.random() > 0.35:
                    continue
                when = day + timedelta(minutes=rng.randint(0, 1439))
                if when > now:
                    continue
                v = versions[app]
                previous = f"v{v[0]}.{v[1]}.{v[2]}"
                v[2] += 1
                trigger = rng.choices(["renovate", "merge", "manual"], [6, 3, 1])[0]
                outcome = rng.choices(
                    ["success", "untested", "rolled_back", "failed"], [85, 6, 5, 4]
                )[0]
                rows.append(
                    {
                        "TimeGenerated": when,
                        "Repo": repo,
                        "App": app,
                        "Version": f"v{v[0]}.{v[1]}.{v[2]}",
                        "PreviousVersion": previous,
                        "Outcome": outcome,
                        "Trigger": trigger,
                        "LeadTimeSec": rng.randint(180, 2400) if trigger != "manual" else None,
                        "CommitSha": f"{rng.getrandbits(40):010x}",
                        "RunUrl": f"https://github.com/jay-withers/{repo}/actions",
                    }
                )
            day += timedelta(days=1)
        cutoff = now - timespan
        rows = [r for r in rows if r["TimeGenerated"] >= cutoff]
        rows.sort(key=lambda r: r["TimeGenerated"], reverse=True)
        return rows


JOBS = [
    "gymlog-garmin",
    "gymlog-insight",
    "gymlog-backup",
    "finances-backup",
    "career-backup",
    "repoagent-scan",
    "sectest-scan",
]
ROOTS = [
    "azure-landingzone/hub",
    "azure-container-apps",
    "cloudflare",
    "github-repos",
    "gym-log",
    "finances",
    "career",
    "observability",
]
HOSTS = [
    "gym.jaywithers.uk",
    "finances.jaywithers.uk",
    "career.jaywithers.uk",
    "observability.jaywithers.uk",
]
SESSIONS = [
    "firewall",
    "aks",
    "vm",
    "bastion",
    "vpn-gateway",
    "app-gateway",
    "nat-gateway",
    "workload-profile",
]


def _ops(now: datetime, timespan: timedelta, rng: random.Random) -> list[Row]:
    rows: list[Row] = []
    days = max(1, timespan.days)
    for back in range(days - 1, -1, -1):
        when = (now - timedelta(days=back)).replace(hour=6, minute=0, second=0, microsecond=0)
        if when > now:
            when = now - timedelta(minutes=5)

        def add(kind: str, name: str, status: str, detail: str, when: datetime = when) -> None:
            rows.append(
                {
                    "TimeGenerated": when,
                    "Kind": kind,
                    "Name": name,
                    "Status": status,
                    "Detail": detail,
                    "RunUrl": "https://github.com/jay-withers/observability/actions",
                    "Repo": "observability",
                }
            )

        for job in JOBS:
            late = job == "sectest-scan" and back < 2
            add(
                "heartbeat",
                job,
                "overdue" if late else "ok",
                "Last success 2 days ago. Overdue after 36h." if late else "Last success today.",
            )
        for check in ("cost-feed", "expiry", "session"):
            add("heartbeat", f"check: {check}", "ok", "Last row today.")
        for root in ROOTS:
            drifted = root == "cloudflare" and back in (3, 4, 5)
            status = "drifted" if drifted else ("failed" if rng.random() < 0.02 else "ok")
            add(
                "drift",
                root,
                status,
                {
                    "ok": "No changes.",
                    "drifted": "2 to change.",
                    "failed": "Plan failed: see the run.",
                }[status],
            )
        for i, host in enumerate(HOSTS):
            left = 74 - back + i * 9 if i else 19 - back // 4
            add(
                "expiry",
                host,
                "expiring" if left < 21 else "ok",
                f"Certificate expires {(now + timedelta(days=left)).date()} ({left} days).",
            )
        for cat in SESSIONS:
            running = cat == "bastion" and back == 0
            add(
                "session",
                cat,
                "running" if running else "ok",
                "Still running: rg-hub-dev/bas-hub. Tag session-until=YYYY-MM-DD to keep it."
                if running
                else "Nothing running.",
            )
        add("cost-feed", "Costs_CL", "ok", "Posted 184 Costs_CL row(s).")
    return rows


def _failed_runs(now: datetime) -> list[Row]:
    return [
        {
            "Job": "sectest-scan",
            "Reason": "BackoffLimitExceeded",
            "Failures": 3,
            "LastFailure": now - timedelta(hours=30),
            "Sample": "Job has reached the specified backoff limit",
        },
        {
            "Job": "gymlog-garmin",
            "Reason": "ContainerCrashing",
            "Failures": 1,
            "LastFailure": now - timedelta(days=4),
            "Sample": "Container 'gymlog' was terminated with exit code 1",
        },
    ]


COST_SHAPE = [
    ("rg-platform-dev", "Log Analytics", 0.55),
    ("rg-platform-dev", "Azure Container Apps", 0.20),
    ("rg-platform-dev", "Storage", 0.04),
    ("rg-gymlog-dev", "Storage", 0.02),
    ("rg-gymlog-dev", "Key Vault", 0.01),
    ("rg-finances-dev", "Azure Database for PostgreSQL", 0.42),
    ("rg-career-dev", "Storage", 0.02),
    ("rg-marketagent-dev", "Azure Container Apps", 0.31),
    ("rg-marketagent-dev", "Storage", 0.05),
    ("rg-aks-dev", "Azure Kubernetes Service", 0.0),
    ("rg-hub-dev", "Azure Bastion", 0.0),
    ("rg-hub-dev", "Virtual Network", 0.03),
]


def _costs(now: datetime, timespan: timedelta, rng: random.Random) -> list[Row]:
    rows: list[Row] = []
    today = now.date()
    # The feed lags a day: yesterday is the newest usage date.
    for back in range(1, timespan.days):
        day = today - timedelta(days=back)
        for rg, service, base in COST_SHAPE:
            cost = base * rng.uniform(0.8, 1.25)
            if rg == "rg-aks-dev" and back % 9 in (2, 3):
                cost = rng.uniform(3.5, 5.0)  # a session left the cluster up
            if rg == "rg-hub-dev" and service == "Azure Bastion" and back in (1, 2):
                cost = 4.6
            if cost <= 0:
                continue
            rows.append(
                {
                    "UsageDate": datetime(day.year, day.month, day.day, tzinfo=UTC),
                    "ResourceGroup": rg,
                    "ServiceName": service,
                    "Cost": round(cost, 4),
                    "Currency": "USD",
                }
            )
    return rows
