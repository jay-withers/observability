namespace Obsview.Tests;

public class SparklineTests
{
    [Fact]
    public void Too_few_points_draw_nothing()
    {
        Assert.Equal("", Sparkline.Path(new List<double?>()));
        Assert.Equal("", Sparkline.Path([3.0]));
        Assert.Equal("", Sparkline.Path(new List<double?> { null, 1.0, null }));
    }

    [Fact]
    public void Line_runs_left_to_right_highest_at_the_top() =>
        Assert.Equal("M2.0,18.0 L98.0,2.0", Sparkline.Path(new List<double?> { 0.0, 10.0 }, width: 100, height: 20));

    [Fact]
    public void Flat_series_sits_mid_height() =>
        Assert.Equal("M2.0,10.0 L98.0,10.0", Sparkline.Path(new List<double?> { 3.0, 3.0 }, width: 100, height: 20));

    [Fact]
    public void Gap_starts_a_new_segment() =>
        Assert.Equal(2, Sparkline.Path(new List<double?> { 1.0, 2.0, null, 3.0, 4.0 }).Count(c => c == 'M'));
}
