using System.Text.RegularExpressions;

namespace Obsview;

/// <summary>One row of <c>OpsEvents_CL</c>: one check's result on one run.</summary>
public sealed record OpsEvent(DateTimeOffset When, string Kind, string Name, string Status, string Detail, string RunUrl)
{
    public int Severity => Ops.Severity.GetValueOrDefault(Status, Ops.Neutral);

    public bool Problem => Ops.Problems.Contains(Status);
}

/// <summary>A job or app Container Apps recorded as failed or crashed.</summary>
public sealed record FailedRun(string Job, string Reason, long Failures, DateTimeOffset Last, string Sample);

public sealed record OpsSection(string Kind, string Title, string Sub, IReadOnlyList<OpsEvent> Current, int Problems)
{
    /// <summary>The status of the section's worst problem, which colours its badge.</summary>
    public string Worst
    {
        get
        {
            var problems = Current.Where(e => e.Problem).ToList();
            // The first of the worst, as Python's max() picks.
            return problems.Count == 0 ? "ok" : problems.First(e => e.Severity == problems.Max(p => p.Severity)).Status;
        }
    }
}

/// <summary>A certificate's gauge: days left, and the bar's width (a year fills it).</summary>
public sealed record Certificate(string Name, int? DaysLeft, string Status, string Detail, double Width);

/// <summary>A count tile: label, count, and the section it jumps to.</summary>
public sealed record OpsTile(string Label, int Count, string Kind);

public sealed record OpsView(
    int Days,
    DateTimeOffset Now,
    IReadOnlyList<OpsEvent> Problems,
    IReadOnlyList<OpsTile> Tiles,
    IReadOnlyList<OpsSection> Sections,
    IReadOnlyList<Certificate> Certificates,
    IReadOnlyList<FailedRun>? FailedRuns,
    OpsCharts Charts);

public sealed record OpsCharts(IReadOnlyList<string> Dates, IReadOnlyDictionary<string, StatusGrid> Grids);

/// <summary>A status grid: row names (reversed for ECharts) and [day, row, severity, status] cells.</summary>
public sealed record StatusGrid(IReadOnlyList<string> Names, IReadOnlyList<object[]> Cells);

/// <summary>
/// The jobs and ops panels, from <c>OpsEvents_CL</c> and failed job runs.
/// <c>OpsEvents_CL</c> holds one row per check per run, written by this
/// repo's scheduled checks and by workflows' drift check: <c>heartbeat</c>
/// (one per Container Apps job, plus <c>check: name</c> for each daily
/// check), <c>drift</c> (one per Terraform root), <c>expiry</c> (one per
/// custom domain), <c>session</c> (one per category of billed-by-the-hour
/// resource) and <c>cost-feed</c>. "Now" is each check's latest row within
/// the last two days, as Grafana's panels read it: a check that stopped
/// writing drops out here, and its <c>check:</c> heartbeat goes overdue instead.
/// </summary>
public static partial class Ops
{
    /// <summary>
    /// How bad a status is, for colour and for "worst of the day". Anything
    /// not listed (a status a future check invents) reads as neutral rather than ok.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Severity = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ok"] = 0,
        ["removed"] = 1,
        ["pending"] = 1,
        ["overdue"] = 2,
        ["drifted"] = 2,
        ["expiring"] = 2,
        ["running"] = 2,
        ["failed"] = 3,
    };

    public const int Neutral = 1;

    /// <summary>The statuses that count as a problem now, as Grafana's "Problems now" did.</summary>
    public static readonly IReadOnlySet<string> Problems = new HashSet<string>(StringComparer.Ordinal)
    {
        "failed", "drifted", "expiring", "overdue", "running",
    };

    public static readonly TimeSpan Fresh = TimeSpan.FromDays(2);

    /// <summary>
    /// The status grid shows at most this many days: at 90 a cell is too thin
    /// to tap on a phone, and a check's history past a month is rarely the question.
    /// </summary>
    public const int GridDays = 30;

    public static readonly IReadOnlyList<(string Kind, string Title, string Sub)> Sections =
    [
        ("heartbeat", "Jobs and checks", "Each job's last success against its usual cadence"),
        ("drift", "Terraform drift", "Nightly plan of every root"),
        ("expiry", "Certificates", "TLS expiry per custom domain"),
        ("session", "Session resources", "Billed-by-the-hour resources left running"),
        ("cost-feed", "Cost feed", "The daily Costs_CL import"),
    ];

    /// <summary>Sections drawn as gauges rather than a status grid.</summary>
    public static readonly IReadOnlySet<string> Gauged = new HashSet<string>(StringComparer.Ordinal) { "expiry" };

    [GeneratedRegex(@"\((\d+) days?\)")]
    private static partial Regex DaysLeftPattern();

    public static string Kql(string environment) =>
        "OpsEvents_CL\n"
        + $"| where Environment == \"{environment}\"\n"
        + "| project TimeGenerated, Kind, Name, Status, Detail, RunUrl, Repo";

    /// <summary>
    /// Container Apps' own record of job executions that failed, and of apps
    /// whose container crashed (the reasons azure-container-apps' crash alert
    /// reads). Summarised in KQL rather than fetched raw: a crash-looping job
    /// or revision writes a row per attempt. The table exists only once
    /// something has logged to it, so it is read through a fuzzy union with an
    /// empty datatable (as azure-container-apps' alerts do): a workspace where
    /// nothing has run yet answers with no rows rather than "Failed to resolve
    /// table". A row names a job or an app, never both, so <c>coalesce</c>
    /// gives the one workload name.
    /// </summary>
    public const string FailedRunsKql = """
        union isfuzzy=true ContainerAppSystemLogs_CL,
            (datatable(TimeGenerated: datetime, JobName_s: string, ContainerAppName_s: string,
                       Reason_s: string, Log_s: string)[])
        | extend Workload = coalesce(JobName_s, ContainerAppName_s)
        | where isnotempty(Workload)
        | where Reason_s in ("BackoffLimitExceeded", "DeadlineExceeded", "ContainerCrashing", "StartError")
        | summarize Failures = count(), LastFailure = max(TimeGenerated), Sample = take_any(Log_s)
            by Job = Workload, Reason = Reason_s
        | order by LastFailure desc
        """;

    public static TimeSpan Window(int days) => TimeSpan.FromDays(Math.Max(Math.Min(days, GridDays), 2));

    public static List<OpsEvent> Parse(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var events = new List<OpsEvent>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("TimeGenerated")) is not { } when)
            {
                continue;
            }

            events.Add(new OpsEvent(when, row.Text("Kind"), row.Text("Name"), row.Text("Status", "unknown"), row.Text("Detail"), row.Text("RunUrl")));
        }

        return events;
    }

    public static List<FailedRun> ParseFailedRuns(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var runs = new List<FailedRun>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("LastFailure")) is not { } last)
            {
                continue;
            }

            var sample = row.Text("Sample");
            runs.Add(new FailedRun(row.Text("Job"), row.Text("Reason"), RowValues.Whole(row.Get("Failures")), last, sample.Length > 300 ? sample[..300] : sample));
        }

        return [.. runs.OrderByDescending(r => r.Last)];
    }

    /// <summary>Each (kind, name)'s newest row, among those from the last two days.</summary>
    public static Dictionary<(string Kind, string Name), OpsEvent> Latest(IEnumerable<OpsEvent> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var current = new Dictionary<(string, string), OpsEvent>();
        foreach (var e in events)
        {
            if (now - e.When > Fresh)
            {
                continue;
            }

            var key = (e.Kind, e.Name);
            if (!current.TryGetValue(key, out var seen) || e.When > seen.When)
            {
                current[key] = e;
            }
        }

        return current;
    }

    /// <summary>Worst first; <c>removed</c> sinks below <c>ok</c>, as in Grafana's tables.</summary>
    private static (int, string) Order(OpsEvent e) => (e.Status != "removed" ? -e.Severity : 1, e.Name);

    private static IEnumerable<OpsEvent> Ordered(IEnumerable<OpsEvent> events) =>
        events.OrderBy(Order, Comparer<(int, string)>.Create((a, b) =>
        {
            var bySeverity = a.Item1.CompareTo(b.Item1);
            return bySeverity != 0 ? bySeverity : string.CompareOrdinal(a.Item2, b.Item2);
        }));

    /// <summary><paramref name="failedRuns"/> null means that query failed: its card says so, the rest renders.</summary>
    public static OpsView Build(IReadOnlyList<OpsEvent> events, IReadOnlyList<FailedRun>? failedRuns, int days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var current = Latest(events, now);

        var sections = new List<OpsSection>();
        foreach (var (kind, title, sub) in Sections)
        {
            var rows = Ordered(current.Where(kv => kv.Key.Kind == kind).Select(kv => kv.Value)).ToList();
            if (rows.Count > 0)
            {
                sections.Add(new OpsSection(kind, title, sub, rows, rows.Count(e => e.Problem)));
            }
        }

        var problems = Ordered(current.Values.Where(e => e.Problem)).ToList();

        int Count(string kind, params string[] statuses) =>
            current.Count(kv => kv.Key.Kind == kind && statuses.Contains(kv.Value.Status));

        var tiles = new List<OpsTile>
        {
            new("Jobs overdue", Count("heartbeat", "overdue"), "heartbeat"),
            new("Roots drifted", Count("drift", "drifted", "failed"), "drift"),
            new("Certificates expiring", Count("expiry", "expiring", "failed"), "expiry"),
            new("Sessions running", Count("session", "running"), "session"),
        };

        var gridDays = Math.Min(days, GridDays);
        var today = Times.Day(now);
        var dates = Enumerable.Range(0, gridDays).Select(i => today.AddDays(-(gridDays - 1 - i))).ToList();
        var grids = sections
            .Where(s => !Gauged.Contains(s.Kind))
            .ToDictionary(s => s.Kind, s => BuildGrid(events, s, dates));
        return new OpsView(
            days,
            now,
            problems,
            tiles,
            sections,
            BuildCertificates(current),
            failedRuns,
            new OpsCharts([.. dates.Select(Times.Iso)], grids));
    }

    /// <summary>Worst status per name per day: [day index, row index, severity, status].</summary>
    private static StatusGrid BuildGrid(IReadOnlyList<OpsEvent> events, OpsSection section, List<DateOnly> dates)
    {
        var names = section.Current.Select(e => e.Name).ToList();
        var index = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var dayIndex = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var worst = new Dictionary<(int Day, int Row), OpsEvent>();
        foreach (var e in events)
        {
            if (e.Kind != section.Kind || !index.TryGetValue(e.Name, out var row))
            {
                continue;
            }

            if (!dayIndex.TryGetValue(Times.Day(e.When), out var day))
            {
                continue;
            }

            var key = (day, row);
            if (!worst.TryGetValue(key, out var seen) || e.Severity > seen.Severity)
            {
                worst[key] = e;
            }
        }

        // Names reversed for ECharts, which draws the first category at the
        // bottom, and each cell's row index flipped to match.
        var last = names.Count - 1;
        var cells = worst
            .OrderBy(kv => kv.Key.Day)
            .ThenBy(kv => kv.Key.Row)
            .Select(kv => new object[] { kv.Key.Day, last - kv.Key.Row, kv.Value.Severity, kv.Value.Status })
            .ToList();
        names.Reverse();
        return new StatusGrid(names, cells);
    }

    private static List<Certificate> BuildCertificates(Dictionary<(string Kind, string Name), OpsEvent> current)
    {
        var certificates = new List<Certificate>();
        foreach (var ((kind, name), e) in current)
        {
            if (kind != "expiry")
            {
                continue;
            }

            var match = DaysLeftPattern().Match(e.Detail);
            int? daysLeft = match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            var width = daysLeft is { } left ? Math.Max(2.0, Math.Min(100.0, 100.0 * left / 365)) : 0.0;
            certificates.Add(new Certificate(name, daysLeft, e.Status, e.Detail, width));
        }

        return [.. certificates
            .OrderBy(c => c.DaysLeft is null)
            .ThenBy(c => c.DaysLeft ?? 0)
            .ThenBy(c => c.Name, StringComparer.Ordinal)];
    }
}
