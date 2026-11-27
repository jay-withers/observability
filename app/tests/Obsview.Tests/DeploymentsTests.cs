namespace Obsview.Tests;

public class DeploymentsTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-17T12:00:00Z");

    internal static Row Row(double hoursAgo, params (string Key, object? Value)[] overrides)
    {
        var row = R.Of(
            ("TimeGenerated", Now.AddHours(-hoursAgo)),
            ("Repo", "gym-log"),
            ("App", "gymlog"),
            ("Version", "v1.0.1"),
            ("PreviousVersion", "v1.0.0"),
            ("Outcome", "success"),
            ("Trigger", "renovate"),
            ("LeadTimeSec", 600L),
            ("CommitSha", "abcdef1234567"),
            ("RunUrl", "https://example.invalid/run"));
        foreach (var (key, value) in overrides)
        {
            row[key] = value;
        }

        return row;
    }

    private static DeploymentsView Build(params Row[] rows) => Deployments.Build(Deployments.Parse(rows), 7, Now);

    private static Kpi K(DeploymentsView view, string label) => view.Kpis.Single(k => k.Label == label);

    [Fact]
    public void Parse_accepts_strings_and_naive_times_and_sorts_newest_first()
    {
        var deploys = Deployments.Parse(
        [
            Row(5, ("TimeGenerated", "2026-11-17T07:00:00Z")),
            Row(1, ("TimeGenerated", new DateTime(2026, 11, 17, 11, 0, 0, DateTimeKind.Unspecified))),
            R.Of(("TimeGenerated", null)),
        ]);
        Assert.Equal([11, 7], deploys.Select(d => d.When.UtcDateTime.Hour));
        Assert.All(deploys, d => Assert.Equal(TimeSpan.Zero, d.When.Offset));
        Assert.Equal("abcdef1", deploys[0].Commit);
    }

    [Fact]
    public void Label_omits_app_named_after_repo()
    {
        var deploys = Deployments.Parse([Row(1, ("App", "gymlog")), Row(2, ("Repo", "finances"), ("App", "finances"))]);
        Assert.Equal("gym-log / gymlog", deploys[0].Label);
        Assert.Equal("finances", deploys[1].Label);
    }

    [Fact]
    public void Untested_counts_as_success_like_the_grafana_panel()
    {
        var view = Build(Row(1), Row(2, ("Outcome", "untested")), Row(3, ("Outcome", "failed")), Row(4, ("Outcome", "rolled_back")));
        Assert.Equal("50.0%", K(view, "Success rate").Value);
        Assert.Equal("1", K(view, "Failed").Value);
        Assert.Equal("1", K(view, "Rolled back").Value);
        Assert.Equal(4, view.Total);
    }

    [Fact]
    public void Previous_period_drives_the_deltas_and_their_tone()
    {
        // Two this week, three the week before.
        var view = Build(Row(24), Row(48), Row(24 * 8, ("Outcome", "failed")), Row(24 * 9), Row(24 * 10));
        var deploys = K(view, "Deploys");
        Assert.Equal("-1 vs prior 7d", deploys.Delta);
        Assert.Equal("neutral", deploys.Tone); // fewer deploys is neither good nor bad
        var failed = K(view, "Failed");
        Assert.Equal("-1 vs prior 7d", failed.Delta);
        Assert.Equal("good", failed.Tone);
        var rate = K(view, "Success rate");
        Assert.Equal("+33.3 pts vs prior 7d", rate.Delta);
        Assert.Equal("good", rate.Tone);
    }

    [Fact]
    public void No_previous_period_means_no_rate_delta()
    {
        var view = Build(Row(1));
        Assert.Null(K(view, "Success rate").Delta);
        Assert.Null(K(view, "Median lead time").Delta);
    }

    [Fact]
    public void Lead_time_delta_reads_as_a_duration_and_shorter_is_good()
    {
        var view = Build(Row(1, ("LeadTimeSec", 300L)), Row(24 * 8, ("LeadTimeSec", 900L)));
        var lead = K(view, "Median lead time");
        Assert.Equal("-10m vs prior 7d", lead.Delta);
        Assert.Equal("good", lead.Tone);
    }

    [Fact]
    public void Rows_outside_the_range_are_not_counted()
    {
        var view = Build(Row(1), Row(24 * 8));
        Assert.Equal(1, view.Total);
        Assert.Equal(1, view.Charts.Outcomes.Values.Sum(v => v.Sum()));
    }

    [Fact]
    public void Median_lead_time_ignores_missing_values()
    {
        var view = Build(Row(1, ("LeadTimeSec", 60L)), Row(2, ("LeadTimeSec", null)), Row(3, ("LeadTimeSec", 300L)));
        Assert.Equal("3m", K(view, "Median lead time").Value);
    }

    [Fact]
    public void Daily_buckets_span_the_range_ending_today()
    {
        var view = Build(Row(1), Row(2), Row(30));
        Assert.Equal(7, view.Charts.Dates.Count);
        Assert.Equal("2026-11-17", view.Charts.Dates[^1]);
        Assert.Equal(2, view.Charts.Outcomes["success"][^1]);
        Assert.Equal(1, view.Charts.Outcomes["success"][^2]);
    }

    [Fact]
    public void Triggers_are_counted_most_common_first()
    {
        var view = Build(Row(1), Row(2), Row(3, ("Trigger", "merge")));
        Assert.Equal([new NamedCount("renovate", 2), new NamedCount("merge", 1)], view.Charts.Triggers);
    }

    [Fact]
    public void Calendar_always_covers_thirteen_weeks()
    {
        var view = Build(Row(1), Row(24 * 60));
        var calendar = view.Charts.Calendar;
        Assert.Equal(Deployments.CalendarDays, calendar.Data.Count);
        Assert.Equal("2026-11-17", calendar.End);
        // The 60-day-old deploy is outside the 7-day range but on the calendar.
        Assert.Equal(2, calendar.Data.Sum(d => (int)d[1]));
    }

    [Fact]
    public void Window_reaches_back_far_enough_for_deltas_and_calendar()
    {
        Assert.Equal(TimeSpan.FromDays(Deployments.CalendarDays), Deployments.Window(7));
        Assert.Equal(TimeSpan.FromDays(180), Deployments.Window(90));
    }

    [Fact]
    public void Timeline_puts_the_busiest_app_on_top()
    {
        var view = Build(Row(1), Row(2), Row(3, ("Repo", "finances"), ("App", "finances")));
        var timeline = view.Charts.Timeline;
        // ECharts draws the first category at the bottom.
        Assert.Equal(["finances", "gym-log / gymlog"], timeline.Apps);
        Assert.Equal(3, timeline.Points.Count);
        Assert.True(string.CompareOrdinal((string)timeline.Points[0][0], (string)timeline.Points[^1][0]) < 0); // oldest first
    }

    [Fact]
    public void Lead_histogram_buckets()
    {
        var seconds = new long[] { 60, 299, 300, 1500, 9999 };
        var view = Build([.. seconds.Select((s, i) => Row(i + 1, ("LeadTimeSec", s)))]);
        Assert.Equal(["0–5m", "5–10m", "10–20m", "20–40m", "40m+"], view.Charts.Lead.Labels);
        Assert.Equal([2, 1, 0, 1, 1], view.Charts.Lead.Counts);
    }

    [Fact]
    public void Apps_sorted_by_last_deploy_with_last_outcome()
    {
        var view = Build(Row(1, ("Repo", "finances"), ("App", "finances"), ("Outcome", "failed")), Row(5), Row(6, ("Outcome", "rolled_back")));
        Assert.Equal(["finances", "gym-log / gymlog"], view.Apps.Select(a => a.Label));
        Assert.Equal("failed", view.Apps[0].Last.Outcome);
        var gym = view.Apps[1];
        Assert.Equal((2, 1, 50.0), (gym.Deploys, gym.RolledBack, gym.SuccessPct));
    }

    [Theory]
    [InlineData(42, "42s")]
    [InlineData(125, "2m 05s")]
    [InlineData(600, "10m")]
    [InlineData(3900, "1h 05m")]
    public void Format_duration(double seconds, string expected) => Assert.Equal(expected, Deployments.FormatDuration(seconds));

    [Fact]
    public void Format_ago()
    {
        Assert.Equal("just now", Deployments.FormatAgo(Now.AddSeconds(-30), Now));
        Assert.Equal("5m ago", Deployments.FormatAgo(Now.AddMinutes(-5), Now));
        Assert.Equal("3h ago", Deployments.FormatAgo(Now.AddHours(-3), Now));
        Assert.Equal("2d ago", Deployments.FormatAgo(Now.AddDays(-2), Now));
    }

    [Fact]
    public void Kql_filters_the_environment_and_leaves_the_window_to_timespan()
    {
        var query = Deployments.Kql("dev");
        Assert.Contains("Environment == \"dev\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("ago(", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiles_and_charts_count_the_same_deploys_at_the_range_edge()
    {
        // Now is 12:00 on Nov 17, so a 7-day range is Nov 11 00:00 to now. A
        // rolling `now - 7d` would have counted the Nov 10 23:00 deploy in the
        // tiles but in no daily bar.
        var view = Build(Row((24 * 6) + 11), Row((24 * 6) + 13), Row(1));
        Assert.Equal("2026-11-11", view.Charts.Dates[0]);
        Assert.Equal(2, view.Total);
        Assert.Equal(view.Total, view.Charts.Outcomes.Values.Sum(v => v.Sum()));
        Assert.Equal("2026-11-11T00:00:00.000Z", view.Charts.Start);
    }

    [Fact]
    public void Js_time_is_a_format_every_browser_parses()
    {
        var when = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);
        Assert.Equal("2026-11-17T10:00:00.123Z", Times.JsTime(when));
        var view = Build(Row(1));
        Assert.Equal("2026-11-17T11:00:00.000Z", view.Charts.Timeline.Points[0][0]);
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData(100.0, "100%")]
    [InlineData(0.0, "0%")]
    [InlineData(66.666, "66.7%")]
    public void Format_pct(double? value, string expected) => Assert.Equal(expected, Deployments.FormatPct(value));
}
