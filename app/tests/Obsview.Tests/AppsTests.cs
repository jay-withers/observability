namespace Obsview.Tests;

public class AppsTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-17T12:00:00Z");
    private static readonly App Gym = Apps.Find("health")!;

    private static Row Event(string kind, string name, string status = "ok") =>
        R.Of(("TimeGenerated", Now), ("Kind", kind), ("Name", name), ("Status", status), ("Detail", ""));

    private static List<string> Names(params Row[] rows) => [.. Apps.EventsFor(Gym, Ops.Parse(rows)).Select(e => e.Name)];

    [Fact]
    public void Every_slug_is_found_and_unknown_is_null()
    {
        Assert.All(Apps.All, a => Assert.Same(a, Apps.Find(a.Slug)));
        Assert.Null(Apps.Find("nope"));
    }

    [Fact]
    public void Workloads_match_on_the_container_apps_naming()
    {
        Assert.True(Gym.OwnsWorkload("ca-gymlog-dev"));
        Assert.True(Gym.OwnsWorkload("caj-gymlog-dev-backup"));
        Assert.True(Gym.OwnsWorkload("CAJ-GYMLOG-DEV-GARMIN"));
        Assert.False(Gym.OwnsWorkload("gymlog-dev"));
        Assert.False(Gym.OwnsWorkload("ca-gymlogx-dev"));
    }

    [Fact]
    public void One_apps_prefix_never_matches_anothers()
    {
        foreach (var a in Apps.All)
        {
            foreach (var b in Apps.All.Where(b => !ReferenceEquals(a, b)))
            {
                Assert.False(a.OwnsWorkload($"caj-{b.Prefix}-dev-job"), $"{a.Slug} owns {b.Slug}'s job");
                Assert.False(a.OwnsRoot($"{b.Slug}/terraform"), $"{a.Slug} owns {b.Slug}'s root");
            }
        }
    }

    [Fact]
    public void Ops_events_by_root_job_check_and_host() =>
        Assert.Equal(
            ["health/terraform", "caj-gymlog-dev-backup", "check: drift gym-log/terraform", "health.jaywithers.uk"],
            Names(
                Event("drift", "health/terraform"),
                Event("drift", "health-extra/terraform"),
                Event("drift", "azure-landingzone/hub"),
                Event("heartbeat", "caj-gymlog-dev-backup"),
                Event("heartbeat", "caj-finances-dev-backup"),
                Event("heartbeat", "check: drift gym-log/terraform"),
                Event("heartbeat", "check: expiry"),
                Event("heartbeat", "Deployments_CL"),
                Event("expiry", "health.jaywithers.uk"),
                Event("expiry", "finances.jaywithers.uk"),
                Event("session", "bastion"),
                Event("cost-feed", "Costs_CL")));

    [Fact]
    public void Failed_runs_and_crashes_by_workload()
    {
        FailedRun[] runs =
        [
            new("caj-gymlog-dev-garmin", "BackoffLimitExceeded", 1, Now, ""),
            new("ca-gymlog-dev", "ContainerCrashing", 2, Now, ""),
            new("caj-sectest-dev-zap", "BackoffLimitExceeded", 1, Now, ""),
        ];
        Assert.Equal(["caj-gymlog-dev-garmin", "ca-gymlog-dev"], Apps.FailedRunsFor(Gym, runs)!.Select(r => r.Job));
        Assert.Null(Apps.FailedRunsFor(Gym, null));
    }

    [Fact]
    public void Costs_by_resource_group()
    {
        var costs = Costs.Parse(
        [
            R.Of(("UsageDate", Now), ("ResourceGroup", "RG-GYMLOG-DEV"), ("ServiceName", "s"), ("Cost", 1.0)),
            R.Of(("UsageDate", Now), ("ResourceGroup", "rg-platform-dev"), ("ServiceName", "s"), ("Cost", 9.0)),
        ]);
        Assert.Equal([1.0], Apps.CostsFor(Gym, costs).Select(c => c.Amount));
    }

    [Fact]
    public void Summary_counts_the_range_and_says_when_a_query_failed()
    {
        var deploys = Deployments.Parse(
        [
            R.Of(("TimeGenerated", Now.AddHours(-1)), ("Repo", "health"), ("Outcome", "success")),
            R.Of(("TimeGenerated", Now.AddHours(-2)), ("Repo", "health"), ("Outcome", "failed")),
            R.Of(("TimeGenerated", Now.AddDays(-9)), ("Repo", "health"), ("Outcome", "success")),
            R.Of(("TimeGenerated", Now), ("Repo", "finances"), ("Outcome", "success")),
        ]);
        var current = Ops.Latest(Ops.Parse([Event("heartbeat", "caj-gymlog-dev-backup", "overdue")]), Now).Values.ToList();
        var totals = new Dictionary<string, int> { ["ca-gymlog-dev"] = 4, ["caj-gymlog-dev-backup"] = 1, ["ca-career-dev"] = 9 };
        var since = Deployments.RangeStart(7, Now);
        Assert.Equal(R.At("2026-11-11T00:00:00Z"), since);
        var s = Apps.Summarise(Gym, deploys, since, current, 1.5, totals);
        Assert.Equal((2, (double?)50.0, (int?)1, (double?)1.5, (int?)5), (s.Deploys, s.SuccessPct, s.Problems, s.Spend, s.Errors));
        Assert.Equal("success", s.Last!.Outcome);
        s = Apps.Summarise(Gym, deploys, since, null, null, null);
        Assert.Equal(((int?)null, (double?)null, (int?)null), (s.Problems, s.Spend, s.Errors));
    }

    // --- the platform --------------------------------------------------------

    [Fact]
    public void Platform_events_are_those_no_app_owns() =>
        Assert.Equal(
            ["azure-landingzone/hub", "cloudflare/terraform", "check: drift azure-container-apps/terraform", "example.jaywithers.uk", "firewall", "Costs_CL"],
            Apps.PlatformEvents(Ops.Parse(
            [
                Event("drift", "azure-landingzone/hub"),
                Event("drift", "cloudflare/terraform"),
                Event("drift", "health/terraform"),
                Event("heartbeat", "caj-gymlog-dev-backup"),
                Event("heartbeat", "check: drift azure-container-apps/terraform"),
                Event("heartbeat", "check: drift finances/terraform"),
                Event("expiry", "health.jaywithers.uk"),
                Event("expiry", "example.jaywithers.uk"),
                Event("session", "firewall"),
                Event("cost-feed", "Costs_CL"),
            ])).Select(e => e.Name));

    [Fact]
    public void Every_event_is_an_apps_or_the_platforms_never_both()
    {
        var rows = Apps.All.Select(a => Event("drift", $"{a.Slug}/terraform"))
            .Concat(Apps.All.Select(a => Event("heartbeat", $"caj-{a.Prefix}-dev-job")))
            .Append(Event("drift", "azure-landingzone/hub"))
            .Append(Event("session", "vm"));
        var parsed = Ops.Parse(rows);
        var platform = Apps.PlatformEvents(parsed);
        foreach (var e in parsed)
        {
            var owners = Apps.All.Count(a => Apps.OwnsEvent(a, e));
            Assert.True(owners + (platform.Contains(e) ? 1 : 0) == 1, e.Name);
        }
    }

    [Fact]
    public void Platform_failed_runs_leave_out_app_workloads()
    {
        Assert.Null(Apps.PlatformFailedRuns(null));
        var runs = new[] { "caj-gymlog-dev-backup", "ca-finances-dev", "caj-platform-dev-sweep" }.Select(j => new FailedRun(j, "Error", 1, Now, ""));
        Assert.Equal(["caj-platform-dev-sweep"], Apps.PlatformFailedRuns(runs)!.Select(r => r.Job));
    }

    [Fact]
    public void Platform_costs_are_the_groups_no_app_owns()
    {
        var rows = Costs.Parse(new[] { "rg-platform-dev", "RG-GYMLOG-DEV", "rg-aks-dev", "rg-hub-dev" }
            .Select(rg => R.Of(("UsageDate", Now), ("ResourceGroup", rg), ("ServiceName", "s"), ("Cost", 1.0), ("Currency", "USD"))));
        Assert.Equal(["rg-platform-dev", "rg-aks-dev", "rg-hub-dev"], Apps.PlatformCosts(rows).Select(c => c.ResourceGroup));
        Assert.Equal(["rg-gymlog-dev"], Apps.CostsFor(Gym, rows).Select(c => c.ResourceGroup));
    }

    /// <summary>
    /// health was gym-log until 2026-11-21, and rows from before then say so.
    /// Without <see cref="App.Former"/>, its old drift rows would be unowned,
    /// and unowned is the platform's.
    /// </summary>
    [Fact]
    public void A_renamed_repos_rows_stay_with_its_app()
    {
        Assert.Same(Gym, Apps.Find("gym-log"));
        Assert.True(Gym.OwnsRoot("gym-log/terraform"));
        Assert.False(Gym.OwnsRoot("gym-log-extra/terraform"));
        Assert.Empty(Apps.PlatformEvents(Ops.Parse([Event("drift", "gym-log/terraform", "drifted")])));
    }

    [Fact]
    public void No_apps_former_name_is_anothers()
    {
        var names = Apps.All.SelectMany(a => a.Repos).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>The deploy workflow's <c>App</c> defaults to the repo, so old rows say gym-log twice.</summary>
    [Fact]
    public void A_former_names_deploys_read_as_the_app_so_they_make_one_lane()
    {
        var mine = Apps.DeploysFor(Gym, Apps.ParseDeploys(
        [
            R.Of(("TimeGenerated", Now), ("Repo", "health"), ("App", "health"), ("Outcome", "success")),
            R.Of(("TimeGenerated", Now.AddDays(-1)), ("Repo", "gym-log"), ("App", "gym-log"), ("Outcome", "failed")),
            R.Of(("TimeGenerated", Now.AddDays(-2)), ("Repo", "gym-log"), ("Outcome", "success")),
            // An App named for something else is kept, as the label shows it.
            R.Of(("TimeGenerated", Now.AddDays(-3)), ("Repo", "gym-log"), ("App", "worker"), ("Outcome", "success")),
            R.Of(("TimeGenerated", Now), ("Repo", "finances"), ("Outcome", "success")),
        ]));
        Assert.Equal(["health", "health", "health", "health / worker"], mine.Select(d => d.Label));
        Assert.Equal(["health", "health / worker"], Deployments.Build(mine, 7, Now).Apps.Select(a => a.Label));
    }
}
