from obsview.sparkline import sparkline


def test_too_few_points_draw_nothing() -> None:
    assert sparkline([]) == ""
    assert sparkline([3.0]) == ""
    assert sparkline([None, 1.0, None]) == ""


def test_line_runs_left_to_right_highest_at_the_top() -> None:
    path = sparkline([0.0, 10.0], width=100, height=20)
    assert path == "M2.0,18.0 L98.0,2.0"


def test_flat_series_sits_mid_height() -> None:
    assert sparkline([3.0, 3.0], width=100, height=20) == "M2.0,10.0 L98.0,10.0"


def test_gap_starts_a_new_segment() -> None:
    path = sparkline([1.0, 2.0, None, 3.0, 4.0])
    assert path.count("M") == 2
