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
    ("observability", "grafana"),
    ("repo-agent", "repoagent"),
    ("security-testing", "scanner"),
]


class DemoLogs:
    def __init__(self, now: datetime | None = None, seed: int = 7) -> None:
        self._now = now
        self._seed = seed

    def query(self, kql: str, timespan: timedelta) -> list[Row]:
        if not kql.lstrip().startswith("Deployments_CL"):
            return []
        now = self._now or datetime.now(UTC)
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
