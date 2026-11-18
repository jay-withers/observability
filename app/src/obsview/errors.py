"""App errors, from Container Apps' console logs.

Every app and job writes stdout and stderr to `ContainerAppConsoleLogs_CL`.
A line is an error when it carries Python's or uvicorn's `ERROR` or
`CRITICAL` level, or is an access-log line with a 5xx status. A traceback's
own lines are left out: the `ERROR` line before it names the error, and each
frame is a row of its own.

The query returns each workload's newest lines raw, capped per workload so
one crash loop can't crowd every other app out, with the workload's true
total alongside. Grouping is done here, so the rule for "the same error" is
tested: the first line with UUIDs, hex ids, numbers and quoted values
masked.

Read through a fuzzy union with an empty datatable, as the failed-runs query
is, so a workspace with no console logs yet answers with no rows. Job rows
name the job in `ContainerJobName_s`, read with `column_ifexists` in case
the table has no job rows yet.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime, timedelta

from .logs import Row
from .times import parse_time

# The most lines fetched per workload: a crash loop logs the same error
# thousands of times. Written into KQL below as a literal; a test keeps the two
# in step.
PER_WORKLOAD = 300

KQL = """let lines = union isfuzzy=true ContainerAppConsoleLogs_CL,
    (datatable(TimeGenerated: datetime, ContainerAppName_s: string, Log_s: string)[])
| extend Workload = coalesce(column_ifexists("ContainerJobName_s", ""), ContainerAppName_s)
| where isnotempty(Workload)
| where Log_s has_cs "ERROR" or Log_s has_cs "CRITICAL"
    or Log_s matches regex @'HTTP/[0-9.]+" 5[0-9][0-9]';
lines
| summarize Total = count() by Workload
| join kind=inner (
    lines
    | sort by Workload asc, TimeGenerated desc
    | extend Rank = row_number(1, prev(Workload) != Workload)
    | where Rank <= 300
  ) on Workload
| project TimeGenerated, Workload, Log_s, Total"""

_UUID = re.compile(r"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", re.I)
_HEX = re.compile(r"\b(?=[0-9a-f]*\d)[0-9a-f]{8,}\b", re.I)
_DIGITS = re.compile(r"\d+")
_QUOTED = re.compile(r"'[^']*'|\"[^\"]*\"")
# A leading timestamp, as many loggers write one.
_STAMP = re.compile(r"^\s*\[?\d{4}-\d\d-\d\d[T ][\d:.,]+Z?\]?\s*")
# Access-log lines are grouped by method, path and status only.
_ACCESS = re.compile(r'"(?P<method>[A-Z]+) (?P<path>[^ ?"]*)[^"]*" (?P<status>5\d\d)')


def window(since: datetime, now: datetime) -> timedelta:
    """Back to `since`, the range's first midnight as the other views count
    it, rounded up to five minutes so the cache key holds still between
    loads. Lines before `since` in that margin are dropped by the caller."""
    step = 300
    seconds = (now - since).total_seconds()
    return timedelta(seconds=-(-seconds // step) * step)


@dataclass(frozen=True)
class ErrorLine:
    when: datetime
    workload: str
    message: str


@dataclass
class ErrorGroup:
    workload: str
    signature: str
    sample: str
    count: int
    first: datetime
    last: datetime


def _mask_ids(text: str) -> str:
    """UUIDs first, so their hex segments aren't masked piecemeal."""
    return _DIGITS.sub("N", _HEX.sub("…", _UUID.sub("…", text)))


def totals(rows: list[Row]) -> dict[str, int]:
    """Each workload's true count of error lines, past the per-workload cap."""
    out: dict[str, int] = {}
    for row in rows:
        workload = str(row.get("Workload") or "")
        total = row.get("Total")
        out[workload] = int(total) if isinstance(total, int | float) else out.get(workload, 0) + 1
    return out


def parse(rows: list[Row]) -> list[ErrorLine]:
    lines = []
    for row in rows:
        when = parse_time(row.get("TimeGenerated"))
        if when is None:
            continue
        lines.append(
            ErrorLine(
                when=when,
                workload=str(row.get("Workload") or ""),
                message=str(row.get("Log_s") or "").strip(),
            )
        )
    return lines


def signature(message: str) -> str:
    """What makes two lines the same error: the first line, masked."""
    first = message.splitlines()[0] if message else ""
    first = _STAMP.sub("", first)
    access = _ACCESS.search(first)
    if access:
        return f"{access['method']} {_mask_ids(access['path'])} → {access['status']}"
    return _mask_ids(_QUOTED.sub("…", first))[:200]


def group(lines: list[ErrorLine]) -> list[ErrorGroup]:
    """One group per workload and signature, the most recent first."""
    groups: dict[tuple[str, str], ErrorGroup] = {}
    for line in lines:
        key = (line.workload, signature(line.message))
        g = groups.get(key)
        if g is None:
            groups[key] = ErrorGroup(*key, line.message[:300], 1, line.when, line.when)
            continue
        g.count += 1
        if line.when > g.last:
            g.last, g.sample = line.when, line.message[:300]
        g.first = min(g.first, line.when)
    return sorted(groups.values(), key=lambda g: g.last, reverse=True)
