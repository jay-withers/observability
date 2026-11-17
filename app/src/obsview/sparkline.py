"""Inline SVG sparklines for the stat tiles, drawn server-side.

A tile is too small for a chart library to earn its keep, and a path string
in the HTML paints with the first byte rather than after a script runs.
"""

from __future__ import annotations

from collections.abc import Sequence

WIDTH = 120
HEIGHT = 32
PAD = 2


def sparkline(values: Sequence[float | None], width: int = WIDTH, height: int = HEIGHT) -> str:
    """An SVG path `d` through `values`, left to right, gaps where None.

    Empty when there are fewer than two points to draw a line between. A flat
    series sits on the middle line rather than the floor, so "always 3" does
    not read as "always 0".
    """
    points = [(i, v) for i, v in enumerate(values) if v is not None]
    if len(points) < 2 or len(values) < 2:
        return ""
    lo = min(v for _, v in points)
    hi = max(v for _, v in points)
    span = hi - lo
    step = (width - 2 * PAD) / (len(values) - 1)

    def y(v: float) -> float:
        if not span:
            return height / 2
        return PAD + (height - 2 * PAD) * (1 - (v - lo) / span)

    parts = []
    previous = None
    for i, v in points:
        command = "L" if previous is not None and i == previous + 1 else "M"
        parts.append(f"{command}{PAD + i * step:.1f},{y(v):.1f}")
        previous = i
    return " ".join(parts)
