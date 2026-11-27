namespace Obsview.Tests;

public class ErrorsTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-17T12:00:00Z");

    [Fact]
    public void Numbers_ids_and_quoted_values_are_masked()
    {
        Assert.Equal(
            Errors.Signature("ERROR sqlalchemy.pool: connection 41 was closed for 'alice'"),
            Errors.Signature("ERROR sqlalchemy.pool: connection 7 was closed for 'bob'"));
        Assert.Equal(Errors.Signature("ERROR job deadbeefcafe0001 failed"), Errors.Signature("ERROR job 0123456789abcdef failed"));
    }

    [Fact]
    public void A_leading_timestamp_is_ignored() =>
        Assert.Equal(Errors.Signature("2026-11-17T10:00:01.123Z ERROR boom"), Errors.Signature("[2026-11-16 09:00:00,456] ERROR boom"));

    [Fact]
    public void Access_lines_group_by_method_path_and_status() =>
        Assert.Equal("GET /sessions/N → 500", Errors.Signature("INFO:     10.0.0.4:5123 - \"GET /sessions/12?x=1 HTTP/1.1\" 500 Internal Server Error"));

    [Fact]
    public void Only_the_first_line_counts() =>
        Assert.Equal("ERROR boom", Errors.Signature("ERROR boom\n  File 'a.py', line 3"));

    [Fact]
    public void Groups_count_and_keep_the_latest_sample_newest_first()
    {
        var groups = Errors.Group(Errors.Parse(
        [
            R.Of(("TimeGenerated", Now.AddHours(-3)), ("Workload", "ca-a"), ("Log_s", "ERROR x 1")),
            R.Of(("TimeGenerated", Now.AddHours(-1)), ("Workload", "ca-a"), ("Log_s", "ERROR x 2")),
            R.Of(("TimeGenerated", Now.AddHours(-2)), ("Workload", "ca-b"), ("Log_s", "ERROR x 3")),
            R.Of(("TimeGenerated", Now), ("Workload", "ca-a"), ("Log_s", "CRITICAL y")),
            R.Of(("TimeGenerated", "not a time"), ("Workload", "ca-a"), ("Log_s", "ERROR z")),
        ]));
        Assert.Equal(
            [("ca-a", "CRITICAL y", 1), ("ca-a", "ERROR x N", 2), ("ca-b", "ERROR x N", 1)],
            groups.Select(g => (g.Workload, g.Signature, g.Count)));
        Assert.Equal("ERROR x 2", groups[1].Sample);
        Assert.Equal(Now.AddHours(-3), groups[1].First);
    }

    [Fact]
    public void Query_reads_fuzzily_and_caps_each_workload()
    {
        Assert.Contains("union isfuzzy=true ContainerAppConsoleLogs_CL", Errors.Kql, StringComparison.Ordinal);
        Assert.Contains($"where Rank <= {Errors.PerWorkload}", Errors.Kql, StringComparison.Ordinal);
    }

    [Fact]
    public void Uuids_are_masked_whole()
    {
        var a = Errors.Signature("ERROR job 3f2a9c1e-ab4d-4ef0-9abc-0123456789ab failed");
        var b = Errors.Signature("ERROR job 00000000-cd5e-11ee-8abc-ffffffffffff failed");
        Assert.Equal("ERROR job … failed", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Ids_in_access_paths_are_masked()
    {
        var a = Errors.Signature("\"GET /runs/ab12cd34ef/x HTTP/1.1\" 500");
        var b = Errors.Signature("\"GET /runs/9f8e7d6c5b/x HTTP/1.1\" 500");
        Assert.Equal("GET /runs/…/x → 500", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Totals_come_from_the_query_past_the_cap() =>
        Assert.Equal(
            new Dictionary<string, int> { ["ca-a"] = 5000, ["ca-b"] = 2 },
            Errors.Totals([R.Of(("Workload", "ca-a"), ("Total", 5000L)), R.Of(("Workload", "ca-a"), ("Total", 5000L)), R.Of(("Workload", "ca-b")), R.Of(("Workload", "ca-b"))]));

    [Fact]
    public void Window_reaches_the_ranges_first_midnight_in_five_minute_steps()
    {
        var since = R.At("2026-11-17T00:00:00Z");
        Assert.Equal(TimeSpan.FromMinutes(65), Errors.Window(since, since.AddHours(1).AddSeconds(1)));
        Assert.Equal(TimeSpan.FromHours(1), Errors.Window(since, since.AddHours(1)));
    }

    [Fact]
    public void A_long_signature_is_cut_at_two_hundred_characters() =>
        Assert.Equal(200, Errors.Signature("ERROR " + new string('x', 400)).Length);
}
