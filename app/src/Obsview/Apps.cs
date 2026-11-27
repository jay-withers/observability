namespace Obsview;

/// <summary>
/// An app: its slug (the repo name), title, Terraform <c>project_name</c>
/// prefix, custom domains, whether it serves HTTP, and the names its repo had
/// before a rename.
/// </summary>
public sealed record App(string Slug, string Title, string Prefix, IReadOnlyList<string>? Hosts = null, bool Serves = true, IReadOnlyList<string>? Former = null)
{
    public IReadOnlyList<string> HostNames => Hosts ?? [];

    public IReadOnlyList<string> FormerNames => Former ?? [];

    /// <summary>Every repo name this app's rows can carry: the slug, then <see cref="Former"/>.</summary>
    public IReadOnlyList<string> Repos => [Slug, .. FormerNames];

    public string ResourceGroup => $"rg-{Prefix}-dev";

    /// <summary>A container app or job of this app's, by Container Apps' name.</summary>
    public bool OwnsWorkload(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = name.ToLowerInvariant();
        return lower.StartsWith($"ca-{Prefix}-", StringComparison.Ordinal) || lower.StartsWith($"caj-{Prefix}-", StringComparison.Ordinal);
    }

    /// <summary>A Terraform root (<c>repo/directory</c>) in this app's repo.</summary>
    public bool OwnsRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Repos.Any(r => root == r || root.StartsWith(r + "/", StringComparison.Ordinal));
    }
}

/// <summary>One app's card on the index. Null figures mean that query failed.</summary>
public sealed record Summary(App App, int Deploys, double? SuccessPct, Deploy? Last, int? Problems, double? Spend, int? Errors);

/// <summary>
/// The apps, and how each table's rows are tied to one of them. Each table
/// names an app its own way, so the tie is explicit here, as
/// <see cref="Costs.Areas"/> is for resource groups:
/// <c>Deployments_CL</c> by <c>Repo</c>, the repo name, which is the app's
/// slug (rows from before a rename carry a former name, which
/// <see cref="ParseDeploys"/> reads as the slug); <c>OpsEvents_CL</c> drift
/// rows by root (<c>repo/directory</c>), heartbeats by Container Apps job
/// (<c>caj-prefix-dev-job</c>) or check (<c>check: drift repo/dir</c>), and
/// expiry rows by host; <c>Costs_CL</c> by resource group; Container Apps' own
/// logs by workload name. <c>prefix</c> is the repo's Terraform
/// <c>project_name</c>, which a rename leaves alone (health's is still
/// <c>gymlog</c>). Rows no app owns are the platform's: its page is the
/// complement of every app's.
/// </summary>
public static class Apps
{
    public static readonly IReadOnlyList<App> All =
    [
        new("health", "Health", "gymlog", ["health.jaywithers.uk"], Former: ["gym-log"]),
        new("finances", "Finances", "finances", ["finances.jaywithers.uk"]),
        new("career", "Career", "career"),
        new("market-agent", "Market agent", "marketagent", ["marketagent.jaywithers.uk"]),
        new("security-testing", "Security testing", "sectest", Serves: false),
        new("repo-agent", "Repo agent", "repoagent", Serves: false),
        new("observability", "Observability", "observability", ["observability.jaywithers.uk"]),
    ];

    private static readonly Dictionary<string, App> BySlug = All.SelectMany(a => a.Repos.Select(r => (r, a))).ToDictionary(x => x.r, x => x.a, StringComparer.Ordinal);

    private static readonly Dictionary<string, App> ByFormer = All.SelectMany(a => a.FormerNames.Select(r => (r, a))).ToDictionary(x => x.r, x => x.a, StringComparer.Ordinal);

    public static App? Find(string slug) => BySlug.GetValueOrDefault(slug);

    /// <summary>
    /// <see cref="Deployments.Parse"/>, with a former repo name read as its
    /// app's slug. The deploy workflow's <c>App</c> defaults to the repo name,
    /// so that moves too when it was the old name. Otherwise one app's deploys
    /// would be two lanes, with two success rates, either side of the rename.
    /// </summary>
    public static List<Deploy> ParseDeploys(IEnumerable<Row> rows) =>
        [.. Deployments.Parse(rows).Select(d => ByFormer.TryGetValue(d.Repo, out var app)
            ? d with { Repo = app.Slug, App = d.App.Length == 0 || d.App == d.Repo ? app.Slug : d.App }
            : d)];

    /// <summary>From <see cref="ParseDeploys"/>, so a former name already reads as the slug.</summary>
    public static List<Deploy> DeploysFor(App app, IEnumerable<Deploy> deploys) => [.. deploys.Where(d => d.Repo == app.Slug)];

    public static bool OwnsEvent(App app, OpsEvent e)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(e);
        const string driftCheck = "check: drift ";
        return e.Kind switch
        {
            "drift" => app.OwnsRoot(e.Name),
            "heartbeat" when e.Name.StartsWith(driftCheck, StringComparison.Ordinal) => app.OwnsRoot(e.Name[driftCheck.Length..]),
            "heartbeat" => app.OwnsWorkload(e.Name),
            "expiry" => app.HostNames.Contains(e.Name),
            _ => false,
        };
    }

    public static List<OpsEvent> EventsFor(App app, IEnumerable<OpsEvent> events) => [.. events.Where(e => OwnsEvent(app, e))];

    public static List<FailedRun>? FailedRunsFor(App app, IEnumerable<FailedRun>? runs) =>
        runs is null ? null : [.. runs.Where(r => app.OwnsWorkload(r.Job))];

    public static List<Cost> CostsFor(App app, IEnumerable<Cost> costs) => [.. costs.Where(c => c.ResourceGroup == app.ResourceGroup)];

    // --- the platform: whatever no app owns ------------------------------------

    public static bool AppWorkload(string name) => All.Any(a => a.OwnsWorkload(name));

    public static List<OpsEvent> PlatformEvents(IEnumerable<OpsEvent> events) => [.. events.Where(e => !All.Any(a => OwnsEvent(a, e)))];

    public static List<FailedRun>? PlatformFailedRuns(IEnumerable<FailedRun>? runs) =>
        runs is null ? null : [.. runs.Where(r => !AppWorkload(r.Job))];

    /// <summary><see cref="Costs.Parse"/> lower-cases the group, as the names here are.</summary>
    public static List<Cost> PlatformCosts(IEnumerable<Cost> costs)
    {
        var groups = All.Select(a => a.ResourceGroup).ToHashSet(StringComparer.Ordinal);
        return [.. costs.Where(c => !groups.Contains(c.ResourceGroup))];
    }

    /// <summary>
    /// <paramref name="deploys"/> newest first; <paramref name="since"/> the
    /// range's first midnight; <paramref name="current"/> each check's latest
    /// row; <paramref name="spend"/> the app's for the range;
    /// <paramref name="errorTotals"/> error lines per workload over the range.
    /// Null means that query failed.
    /// </summary>
    public static Summary Summarise(
        App app,
        IReadOnlyList<Deploy> deploys,
        DateTimeOffset since,
        IReadOnlyList<OpsEvent>? current,
        double? spend,
        IReadOnlyDictionary<string, int>? errorTotals)
    {
        ArgumentNullException.ThrowIfNull(app);
        var mine = DeploysFor(app, deploys);
        var inRange = mine.Where(d => d.When >= since).ToList();
        int? errors = errorTotals?.Where(kv => app.OwnsWorkload(kv.Key)).Sum(kv => kv.Value);
        return new Summary(
            app,
            inRange.Count,
            Deployments.Rate(inRange.Count(d => d.Succeeded), inRange.Count),
            mine.Count > 0 ? mine[0] : null,
            current is null ? null : EventsFor(app, current).Count(e => e.Problem),
            spend,
            errors);
    }

    /// <summary>
    /// <paramref name="app"/>'s SLIs over the SLO window. Null for any input
    /// means its query failed, and the SLIs it feeds read "unavailable".
    /// </summary>
    public static SloReport SloReportFor(
        App app,
        IReadOnlyList<HttpHour>? hours,
        IReadOnlyList<JobRun>? runs,
        IReadOnlyList<Deploy>? deploys,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(app);
        var mineHours = hours?.Where(h => app.OwnsWorkload(h.Workload)).ToList();
        var points = new Dictionary<string, List<Point>?>(StringComparer.Ordinal)
        {
            ["availability"] = mineHours is null ? null : Slo.AvailabilityPoints(mineHours),
            ["latency"] = mineHours is null ? null : Slo.LatencyPoints(mineHours),
            ["jobs"] = runs is null ? null : Slo.JobPoints(runs.Where(r => app.OwnsWorkload(r.Job))),
            ["deploys"] = deploys is null ? null : Slo.DeployPoints(DeploysFor(app, deploys)),
        };
        var slis = Slo.Objectives
            .Where(o => app.Serves || !o.WebOnly)
            .Select(o => Slo.Measure(o, points[o.Key], now))
            .ToList();
        return new SloReport(app.Slug, app.Title, slis);
    }
}
