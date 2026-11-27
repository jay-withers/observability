using System.Text.RegularExpressions;

namespace Obsview.Tests;

public partial class SloTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-20T12:00:00Z");
    private static readonly Objective Availability = Slo.Objectives.Single(o => o.Key == "availability");
    private static readonly Objective Jobs = Slo.Objectives.Single(o => o.Key == "jobs");

    /// <summary>(hours ago, good, total) each.</summary>
    private static List<Point> Points(params (double Hours, long Good, long Total)[] spec) =>
        [.. spec.Select(s => new Point(Now.AddHours(-s.Hours), s.Good, s.Total))];

    [GeneratedRegex(@"Ms <= (\d+)")]
    private static partial Regex Threshold();

    [Fact]
    public void The_latency_threshold_in_the_kql_is_latency_ms()
    {
        Assert.Contains($"Ms <= {Slo.LatencyMs})", Slo.HttpKql, StringComparison.Ordinal);
        Assert.Equal([Slo.LatencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture)], Threshold().Matches(Slo.HttpKql).Select(m => m.Groups[1].Value));
    }

    [Fact]
    public void The_window_stays_inside_the_container_apps_logs_retention()
    {
        Assert.True(Slo.WindowDays <= 30);
        // The deploy query the SLO pages share never reaches back less than the window.
        Assert.True(Deployments.Window(1) >= Slo.Window);
    }

    [Fact]
    public void Budget_is_what_the_target_allows_and_what_is_left()
    {
        // 99.5% of 2,000 requests allows 10 bad; 4 bad leaves 60% of it.
        var sli = Slo.Measure(Availability, Points((1, 996, 1000), (30, 1000, 1000)), Now);
        Assert.Equal(2000, sli.Total);
        Assert.Equal(4, sli.Bad);
        Assert.Equal(10, sli.Allowed, 9);
        Assert.Equal(0.6, sli.BudgetLeft!.Value, 9);
        Assert.Equal(0.998, sli.Ratio!.Value, 9);
        Assert.Equal("ok", sli.Status);
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(7, "ok")]
    [InlineData(8, "at-risk")]
    [InlineData(9, "at-risk")]
    [InlineData(10, "exhausted")]
    [InlineData(25, "exhausted")]
    public void Status_follows_the_budget_left(int bad, string status) =>
        Assert.Equal(status, Slo.Measure(Availability, Points((2, 2000 - bad, 2000)), Now).Status);

    [Fact]
    public void An_overspent_budget_goes_negative() =>
        // 30 bad against 5 allowed: five budgets over.
        Assert.Equal(-5.0, Slo.Measure(Availability, Points((2, 970, 1000)), Now).BudgetLeft!.Value, 9);

    [Fact]
    public void Events_outside_the_window_are_dropped()
    {
        var sli = Slo.Measure(Availability, Points(((Slo.WindowDays * 24) + 1, 0, 500), (1, 100, 100)), Now);
        Assert.Equal(100, sli.Total);
        Assert.Equal("ok", sli.Status);
    }

    [Fact]
    public void No_events_is_no_data_not_ok()
    {
        var sli = Slo.Measure(Availability, [], Now);
        Assert.Equal("no-data", sli.Status);
        Assert.Null(sli.Ratio);
        Assert.Null(sli.BudgetLeft);
        Assert.All(sli.Points, p => Assert.Null(p[1]));
    }

    [Fact]
    public void A_failed_query_is_unavailable() =>
        Assert.Equal("unavailable", Slo.Measure(Availability, null, Now).Status);

    [Fact]
    public void Burn_rates_cover_the_last_day_and_week()
    {
        // Last day: 2% bad against a 0.5% allowance is 4x. The week adds a
        // clean day of equal size: 1% bad, 2x.
        var sli = Slo.Measure(Availability, Points((3, 980, 1000), (100, 1000, 1000)), Now);
        Assert.Equal(4.0, sli.BurnDay!.Value, 9);
        Assert.Equal(2.0, sli.BurnWeek!.Value, 9);
        Assert.Null(Slo.Measure(Availability, Points((100, 1000, 1000)), Now).BurnDay);
    }

    [Fact]
    public void Burndown_is_a_day_a_row_falling_as_bad_events_land()
    {
        var spec = new List<(double, long, long)> { (72, 0, 1), (1, 1, 1) };
        spec.AddRange(Enumerable.Range(5, 15).Select(d => ((double)(24 * d), 1L, 1L)));
        var sli = Slo.Measure(Jobs, Points([.. spec]), Now);
        Assert.Equal(Slo.WindowDays, sli.Points.Count);
        Assert.Equal("2026-11-20", sli.Points[^1][0]);
        var lefts = sli.Points.Select(p => (double)p[1]!).ToList();
        Assert.Equal(lefts.OrderDescending(), lefts);
        Assert.Equal(100, lefts[0]);
        Assert.Equal(100 * sli.BudgetLeft!.Value, lefts[^1], 2);
        var threeDaysAgo = sli.Points.Single(p => (string)p[0]! == Times.Iso(Times.Day(Now.AddHours(-72))));
        Assert.Equal(new object?[] { 1L, 1L }, threeDaysAgo[2..]);
    }

    [Fact]
    public void Job_runs_count_only_finished_runs_and_a_failure_wins()
    {
        var runs = Slo.ParseJobs(
        [
            R.Of(("Job", "caj-gymlog-dev-backup"), ("Started", Now), ("Completed", 1L), ("Failed", 0L)),
            R.Of(("Job", "caj-gymlog-dev-backup"), ("Started", Now), ("Completed", 1L), ("Failed", 1L)),
            R.Of(("Job", "caj-gymlog-dev-backup"), ("Started", Now), ("Completed", 0L), ("Failed", 0L)),
            R.Of(("Job", "caj-gymlog-dev-backup"), ("Started", null), ("Completed", 1L), ("Failed", 0L)),
        ]);
        Assert.Equal([true, false], runs.Select(r => r.Ok));
    }

    [Fact]
    public void Http_hours_split_into_availability_and_latency()
    {
        var hours = Slo.ParseHttp(
        [
            R.Of(("Hour", Now), ("Workload", "ca-gymlog-dev"), ("Total", 10L), ("Errors", 1L), ("Timed", 8L), ("Fast", 6L)),
            R.Of(("Hour", "junk"), ("Workload", "ca-gymlog-dev"), ("Total", 10L)),
        ]);
        Assert.Single(hours);
        Assert.Equal([new Point(Now, 9, 10)], Slo.AvailabilityPoints(hours));
        // Latency counts only the requests with a duration logged.
        Assert.Equal([new Point(Now, 6, 8)], Slo.LatencyPoints(hours));
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData(1.0, "100%")]
    [InlineData(0.99999, "99.99%")]
    [InlineData(0.995, "99.5%")]
    [InlineData(0.9987, "99.87%")]
    [InlineData(0.95, "95%")]
    [InlineData(0.9312, "93.1%")]
    [InlineData(0.5, "50%")]
    public void Ratios_are_truncated_never_rounded_up_to_100(double? ratio, string text) =>
        Assert.Equal(text, Slo.FormatRatio(ratio));

    [Fact]
    public void An_apps_report_is_its_own_rows_and_worst_status()
    {
        var gym = Apps.Find("health")!;
        var agent = Apps.Find("repo-agent")!;
        HttpHour[] hours = [new(Now, "ca-gymlog-dev", 100, 0, 100, 100), new(Now, "ca-finances-dev", 100, 100, 100, 0)];
        JobRun[] runs = [new(Now, "caj-gymlog-dev-backup", false)];
        var report = Apps.SloReportFor(gym, hours, runs, [], Now);
        var byKey = report.Slis.ToDictionary(s => s.Objective.Key);
        Assert.Equal("ok", byKey["availability"].Status);
        Assert.Equal("exhausted", byKey["jobs"].Status);
        Assert.Equal("no-data", byKey["deploys"].Status);
        Assert.Equal("exhausted", report.Status);
        Assert.Same(byKey["jobs"], report.LeastBudget);
        // No deploys: measured leaves it out; a job-only app has no request SLIs.
        Assert.Equal(["availability", "latency", "jobs"], report.Measured.Select(s => s.Objective.Key));
        Assert.Equal(["jobs", "deploys"], Apps.SloReportFor(agent, hours, runs, [], Now).Slis.Select(s => s.Objective.Key));
    }

    [Fact]
    public void A_failed_query_reads_unavailable_not_ok()
    {
        var report = Apps.SloReportFor(Apps.Find("health")!, null, [], [], Now);
        Assert.Equal("unavailable", report.Status);
        Assert.DoesNotContain("availability", report.Charts.Burndown.Keys);
    }

    [Fact]
    public void Tally_counts_apps_by_status()
    {
        SloReport[] reports =
        [
            new("a", "A", [new Sli(Availability, "ok")]),
            new("b", "B", [new Sli(Availability, "ok"), new Sli(Jobs, "at-risk")]),
            new("c", "C", []),
        ];
        Assert.Equal(
            new Dictionary<string, int> { ["exhausted"] = 0, ["at-risk"] = 1, ["unavailable"] = 0, ["ok"] = 1, ["no-data"] = 1 },
            Slo.Tally(reports));
    }

    [Fact]
    public void The_window_is_whole_days_so_the_burndown_ends_on_the_budget_left()
    {
        // Regression: the window was `now - 28d`, so events on the morning of
        // the 29th day back spent budget the burn-down never showed.
        var start = Slo.WindowStart(Now);
        Assert.Equal(R.At("2026-10-24T00:00:00Z"), start);
        Point[] edge =
        [
            new(start.AddSeconds(-1), 0, 100), // outside
            new(start, 990, 1000), // the first hourly bin, counted whole
            new(Now.AddHours(-1), 1000, 1000),
        ];
        var sli = Slo.Measure(Availability, edge, Now);
        Assert.Equal(2000, sli.Total);
        Assert.Equal(new object?[] { 10L, 1000L }, sli.Points[0][2..]);
        Assert.Equal(100 * sli.BudgetLeft!.Value, (double)sli.Points[^1][1]!, 2);
    }

    [Fact]
    public void Burn_rates_count_the_hourly_bin_they_start_in()
    {
        // Regression: the bin starting before `now - 24h` was dropped whole.
        var now = Now.AddMinutes(40);
        var binStart = Now.AddDays(-1);
        Assert.NotNull(Slo.Measure(Availability, [new Point(binStart, 0, 10)], now).BurnDay);
    }

    [Fact]
    public void A_job_run_counts_in_the_window_it_was_created_in()
    {
        // Regression: `Started` was the first event in the window, so a run
        // created before it was dated by its completion and counted.
        Assert.Contains("Started = minif(TimeGenerated, Reason_s == \"SuccessfulCreate\")", Slo.JobsKql, StringComparison.Ordinal);
        Assert.Empty(Slo.ParseJobs([R.Of(("Job", "caj-x-dev-y"), ("Started", null), ("Completed", 1L), ("Failed", 0L))]));
    }

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.476, "47%")]
    [InlineData(0.0, "0%")]
    [InlineData(0.004, "0.4%")]
    [InlineData(0.0004, "<0.1%")]
    [InlineData(-0.004, "0.4%")]
    [InlineData(-1.5, "150%")]
    public void Budgets_are_truncated_and_never_read_as_spent_when_they_are_not(double left, string text) =>
        // Regression: rounded to whole percent, 0.4% left read "0% left" beside an "at risk" badge.
        Assert.Equal(text, Slo.FormatBudget(left));
}
