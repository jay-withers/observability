namespace Obsview.Tests;

public class OpsTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-17T12:00:00Z");

    internal static Row Row(double hoursAgo, string kind, string name, string status = "ok", params (string Key, object? Value)[] extra)
    {
        var row = R.Of(
            ("TimeGenerated", Now.AddHours(-hoursAgo)),
            ("Kind", kind),
            ("Name", name),
            ("Status", status),
            ("Detail", ""),
            ("RunUrl", "https://example.invalid/run"));
        foreach (var (key, value) in extra)
        {
            row[key] = value;
        }

        return row;
    }

    private static OpsView Build(int days, params Row[] rows) => Ops.Build(Ops.Parse(rows), [], days, Now);

    private static OpsView Build(params Row[] rows) => Build(7, rows);

    [Fact]
    public void Now_is_each_checks_latest_row()
    {
        var view = Build(Row(30, "drift", "cloudflare", "drifted"), Row(2, "drift", "cloudflare", "ok"));
        Assert.Empty(view.Problems);
        Assert.Equal("ok", view.Sections[0].Current[0].Status);
    }

    [Fact]
    public void Rows_older_than_two_days_are_not_now()
    {
        var view = Build(Row(49, "drift", "cloudflare", "drifted"));
        Assert.Empty(view.Problems);
        Assert.Empty(view.Sections);
    }

    [Fact]
    public void Problems_match_grafanas_statuses_worst_first()
    {
        var view = Build(
            Row(1, "heartbeat", "a-job", "overdue"),
            Row(1, "drift", "root", "failed"),
            Row(1, "drift", "other", "pending"), // not a problem
            Row(1, "heartbeat", "gone", "removed"), // not a problem
            Row(1, "session", "bastion", "running"));
        Assert.Equal([("root", "failed"), ("a-job", "overdue"), ("bastion", "running")], view.Problems.Select(e => (e.Name, e.Status)));
    }

    [Fact]
    public void Tiles_count_per_kind()
    {
        var view = Build(
            Row(1, "heartbeat", "a", "overdue"),
            Row(1, "heartbeat", "b", "ok"),
            Row(1, "drift", "r", "failed"),
            Row(1, "expiry", "h", "expiring"),
            Row(1, "session", "vm", "ok"));
        Assert.Equal(
            new Dictionary<string, int> { ["Jobs overdue"] = 1, ["Roots drifted"] = 1, ["Certificates expiring"] = 1, ["Sessions running"] = 0 },
            view.Tiles.ToDictionary(t => t.Label, t => t.Count));
    }

    [Fact]
    public void Removed_sinks_below_ok_within_a_section()
    {
        var view = Build(Row(1, "heartbeat", "a", "removed"), Row(1, "heartbeat", "b", "ok"), Row(1, "heartbeat", "c", "overdue"));
        Assert.Equal(["c", "b", "a"], view.Sections[0].Current.Select(e => e.Name));
    }

    [Fact]
    public void Grid_keeps_the_worst_status_of_each_day()
    {
        var view = Build(
            Row(1, "drift", "root", "ok"),
            Row(3, "drift", "root", "failed"), // same day (Nov 17), worse
            Row(26, "drift", "root", "drifted")); // Nov 16
        var grid = view.Charts.Grids["drift"];
        Assert.Equal(["root"], grid.Names);
        Assert.Equal("2026-11-17", view.Charts.Dates[^1]);
        Assert.Equal(new Dictionary<int, string> { [6] = "failed", [5] = "drifted" }, grid.Cells.ToDictionary(c => (int)c[0], c => (string)c[3]));
    }

    [Fact]
    public void Grid_is_capped_at_thirty_days()
    {
        var view = Build(90, Row(1, "drift", "root"));
        Assert.Equal(Ops.GridDays, view.Charts.Dates.Count);
        Assert.Equal(TimeSpan.FromDays(Ops.GridDays), Ops.Window(90));
        Assert.Equal(TimeSpan.FromDays(7), Ops.Window(7));
        Assert.Equal(TimeSpan.FromDays(2), Ops.Window(1));
    }

    [Fact]
    public void Certificates_parse_days_left_and_sort_soonest_first()
    {
        var view = Build(
            Row(1, "expiry", "a.example", "ok", ("Detail", "Certificate expires 2027-01-30 (74 days).")),
            Row(1, "expiry", "b.example", "expiring", ("Detail", "Certificate expires 2026-12-01 (14 days).")),
            Row(1, "expiry", "c.example", "failed", ("Detail", "No TLS certificate from c.example:443.")));
        Assert.Equal([("b.example", (int?)14), ("a.example", 74), ("c.example", null)], view.Certificates.Select(c => (c.Name, c.DaysLeft)));
        Assert.Equal(0.0, view.Certificates[2].Width);
    }

    [Fact]
    public void Unknown_status_reads_as_neutral_not_ok()
    {
        var e = Assert.Single(Ops.Parse([Row(1, "drift", "root", "something-new")]));
        Assert.Equal(Ops.Neutral, e.Severity);
        Assert.False(e.Problem);
    }

    [Fact]
    public void Failed_runs_parse_newest_first()
    {
        var runs = Ops.ParseFailedRuns(
        [
            R.Of(("Job", "a"), ("Reason", "StartError"), ("Failures", 2L), ("LastFailure", Now.AddDays(-2)), ("Sample", "x")),
            R.Of(("Job", "b"), ("Reason", "BackoffLimitExceeded"), ("Failures", 1L), ("LastFailure", "2026-11-17T10:00:00Z")),
            R.Of(("Job", "c"), ("LastFailure", null)),
        ]);
        Assert.Equal(["b", "a"], runs.Select(r => r.Job));
    }

    [Fact]
    public void Kql_filters_the_environment() =>
        Assert.Contains("Environment == \"dev\"", Ops.Kql("dev"), StringComparison.Ordinal);

    [Fact]
    public void Section_badge_takes_the_worst_problem()
    {
        Assert.Equal("overdue", Build(Row(1, "heartbeat", "a", "overdue"), Row(1, "heartbeat", "b", "ok")).Sections[0].Worst);
        Assert.Equal("failed", Build(Row(1, "drift", "a", "drifted"), Row(1, "drift", "b", "failed")).Sections[0].Worst);
        Assert.Equal("ok", Build(Row(1, "drift", "a", "ok")).Sections[0].Worst);
    }

    [Fact]
    public void Expiry_is_drawn_as_gauges_so_has_no_grid()
    {
        var view = Build(Row(1, "expiry", "a.example", "ok", ("Detail", "(40 days)")), Row(1, "drift", "r"));
        Assert.Equal(["drift"], view.Charts.Grids.Keys);
    }

    [Fact]
    public void Failed_runs_query_reads_fuzzily() =>
        Assert.Contains("isfuzzy=true", Ops.FailedRunsKql, StringComparison.Ordinal);

    [Fact]
    public void Malformed_time_skips_the_row() =>
        Assert.Empty(Ops.Parse([Row(1, "drift", "r", "ok", ("TimeGenerated", "yesterday"))]));
}
