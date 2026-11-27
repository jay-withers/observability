namespace Obsview;

public sealed record Objective(string Key, string Title, double Target, string Good, string Unit, bool WebOnly = false);

/// <summary>Some events at a time: an hour of requests, a job run, a deploy.</summary>
public sealed record Point(DateTimeOffset When, long Good, long Total);

public sealed record HttpHour(DateTimeOffset When, string Workload, long Total, long Errors, long Timed, long Fast);

public sealed record JobRun(DateTimeOffset When, string Job, bool Ok);

/// <summary>One SLI over the window: its ratio, error budget, burn rates and burn-down.</summary>
public sealed record Sli(
    Objective Objective,
    string Status,
    long Good = 0,
    long Total = 0,
    double? Ratio = null,
    double Allowed = 0.0,
    double? BudgetLeft = null,
    double? BurnDay = null,
    double? BurnWeek = null,
    IReadOnlyList<object?[]>? Burndown = null)
{
    public long Bad => Total - Good;

    public string Label => Slo.Labels[Status];

    /// <summary>[day, budget left %, bad that day, valid that day], oldest first.</summary>
    public IReadOnlyList<object?[]> Points => Burndown ?? [];
}

/// <summary>One app's SLOs: its tile on /slo, and its page.</summary>
public sealed record SloReport(string Slug, string Title, IReadOnlyList<Sli> Slis)
{
    public string Status => Slo.Worst(Slis.Select(s => s.Status));

    public string Label => Slo.Labels[Status];

    /// <summary>The SLIs with events in the window, or whose query failed.</summary>
    public IReadOnlyList<Sli> Measured => [.. Slis.Where(s => s.Status != "no-data")];

    /// <summary>The SLI with the least budget left: the tile's headline.</summary>
    public Sli? LeastBudget
    {
        get
        {
            var scored = Slis.Where(s => s.BudgetLeft is not null).ToList();
            return scored.Count == 0 ? null : scored.First(s => s.BudgetLeft == scored.Min(x => x.BudgetLeft));
        }
    }

    public SloCharts Charts => new(Measured
        .Where(s => s.Points.Count > 0 && s.Status != "unavailable")
        .ToDictionary(s => s.Objective.Key, s => new Burndown(s.Objective.Title, s.Points)));
}

public sealed record SloCharts(IReadOnlyDictionary<string, Burndown> Burndown);

public sealed record Burndown(string Title, IReadOnlyList<object?[]> Points);

/// <summary>
/// Service level objectives: each app's SLIs against a target, over 28 days.
/// An SLI is always good events over valid events, so every objective has the
/// same arithmetic and the same error budget:
/// availability (requests the ingress answered without a 5xx, from
/// <c>ContainerAppHTTPLogs</c>; a cold start's Envoy 503 counts against it),
/// latency (requests answered within <see cref="LatencyMs"/>, of those with a
/// duration logged), job runs (executions that completed, of those that
/// finished, from <c>ContainerAppSystemLogs_CL</c>) and deploys (deploys that
/// succeeded).
///
/// The window is a rolling <see cref="WindowDays"/> whole UTC days, today the
/// last, whatever the page's range: 28 days stays inside the workspace's
/// 30-day retention for the Container Apps logs. The error budget is the bad
/// events the target allows, <c>(1 - target) * valid</c>; what is left of it
/// is <c>1 - bad / allowed</c>, negative once spent. Status reads only that.
/// Burn rates (1 being exactly on budget) are shown, not alerted on: with a
/// few hundred requests a day one 5xx moves a day's rate a lot, and alerting
/// stays in Azure Monitor. The queries count in KQL by hour and workload, and
/// by job execution; every ratio and budget is computed here.
/// </summary>
public static class Slo
{
    public const int WindowDays = 28;

    public static readonly TimeSpan Window = TimeSpan.FromDays(WindowDays);

    /// <summary>
    /// A request answered within this many milliseconds is fast. Written into
    /// the KQL as a literal; a test keeps the two in step.
    /// </summary>
    public const int LatencyMs = 1000;

    /// <summary>Less than this fraction of the budget left is "at risk".</summary>
    public const double AtRisk = 0.25;

    public const string HttpKql = """
        union isfuzzy=true ContainerAppHTTPLogs,
            (datatable(TimeGenerated: datetime, ContainerAppName: string)[])
        | where isnotempty(ContainerAppName)
        | extend Status = toint(column_ifexists("StatusCode", int(null))),
            Ms = todouble(column_ifexists("DurationMs", real(null)))
        | summarize Total = count(), Errors = countif(Status >= 500),
            Timed = countif(isnotnull(Ms)), Fast = countif(Ms <= 1000)
            by Hour = bin(TimeGenerated, 1h), Workload = ContainerAppName
        """;

    /// <summary>
    /// One row per job execution: when it started, and whether it completed
    /// or failed. <c>Started</c> is the create event's time, null for a run
    /// created before the window, which <see cref="ParseJobs"/> leaves out: a
    /// run counts in the window it began.
    /// </summary>
    public const string JobsKql = """
        union isfuzzy=true ContainerAppSystemLogs_CL,
            (datatable(TimeGenerated: datetime, JobName_s: string, ExecutionName_s: string,
                       Reason_s: string)[])
        | where isnotempty(JobName_s) and isnotempty(ExecutionName_s)
        | where Reason_s in ("SuccessfulCreate", "Completed", "BackoffLimitExceeded", "DeadlineExceeded")
        | summarize Started = minif(TimeGenerated, Reason_s == "SuccessfulCreate"),
            Completed = countif(Reason_s == "Completed"),
            Failed = countif(Reason_s in ("BackoffLimitExceeded", "DeadlineExceeded"))
            by Job = JobName_s, Execution = ExecutionName_s
        """;

    public static readonly IReadOnlyList<Objective> Objectives =
    [
        new("availability", "Availability", 0.995, "Requests answered without a 5xx", "requests", WebOnly: true),
        new("latency", "Latency", 0.95, $"Requests answered within {Fmt.Count(LatencyMs)} ms", "requests", WebOnly: true),
        new("jobs", "Job runs", 0.95, "Job executions that completed", "runs"),
        new("deploys", "Deploys", 0.90, "Deploys that succeeded", "deploys"),
    ];

    /// <summary>Worst first, for an app's overall status and the badge colours.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["exhausted", "at-risk", "unavailable", "ok", "no-data"];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["exhausted"] = "budget spent",
        ["at-risk"] = "at risk",
        ["unavailable"] = "unavailable",
        ["ok"] = "within budget",
        ["no-data"] = "no data",
    };

    public static List<HttpHour> ParseHttp(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var hours = new List<HttpHour>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("Hour")) is not { } when)
            {
                continue;
            }

            hours.Add(new HttpHour(
                when,
                row.Text("Workload"),
                RowValues.Whole(row.Get("Total")),
                RowValues.Whole(row.Get("Errors")),
                RowValues.Whole(row.Get("Timed")),
                RowValues.Whole(row.Get("Fast"))));
        }

        return hours;
    }

    /// <summary>Finished runs only: a run with neither outcome yet is still going.</summary>
    public static List<JobRun> ParseJobs(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var runs = new List<JobRun>();
        foreach (var row in rows)
        {
            var failed = RowValues.Whole(row.Get("Failed"));
            var completed = RowValues.Whole(row.Get("Completed"));
            if (Times.Parse(row.Get("Started")) is not { } when || (failed == 0 && completed == 0))
            {
                continue;
            }

            // Failed wins: a run that completed on a retry after a crash still
            // completed, but one past its backoff limit did not.
            runs.Add(new JobRun(when, row.Text("Job"), failed == 0));
        }

        return runs;
    }

    public static List<Point> AvailabilityPoints(IEnumerable<HttpHour> hours) => [.. hours.Select(h => new Point(h.When, h.Total - h.Errors, h.Total))];

    public static List<Point> LatencyPoints(IEnumerable<HttpHour> hours) => [.. hours.Select(h => new Point(h.When, h.Fast, h.Timed))];

    public static List<Point> JobPoints(IEnumerable<JobRun> runs) => [.. runs.Select(r => new Point(r.When, r.Ok ? 1 : 0, 1))];

    public static List<Point> DeployPoints(IEnumerable<Deploy> deploys) => [.. deploys.Select(d => new Point(d.When, d.Succeeded ? 1 : 0, 1))];

    /// <summary>How fast the points since <paramref name="since"/> spent the budget: 1 is on budget.</summary>
    private static double? Burn(List<Point> points, DateTimeOffset since, double target)
    {
        var recent = points.Where(p => p.When >= since).ToList();
        var total = recent.Sum(p => p.Total);
        return total == 0 ? null : (double)(total - recent.Sum(p => p.Good)) / total / (1 - target);
    }

    public static string StatusOf(double? budgetLeft) => budgetLeft switch
    {
        null => "no-data",
        <= 0 => "exhausted",
        < AtRisk => "at-risk",
        _ => "ok",
    };

    /// <summary>
    /// The window is whole UTC days, today the last: its first midnight. So
    /// the burn-down's days hold exactly the events the totals count, and an
    /// hourly bin is never cut in two at the start.
    /// </summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now) => Times.Midnight(Times.Day(now).AddDays(-(WindowDays - 1)));

    private static DateTimeOffset Hour(DateTimeOffset when)
    {
        var utc = when.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The SLI over the window ending <paramref name="now"/>. Null points means its query failed.</summary>
    public static Sli Measure(Objective objective, IReadOnlyList<Point>? points, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(objective);
        if (points is null)
        {
            return new Sli(objective, "unavailable");
        }

        var since = WindowStart(now);
        var inside = points.Where(p => since <= p.When && p.When <= now).ToList();
        var total = inside.Sum(p => p.Total);
        var good = inside.Sum(p => p.Good);
        var allowed = (1 - objective.Target) * total;
        // Rounded, so a budget spent exactly reads 0 rather than a hair either side.
        double? budgetLeft = total == 0 ? null : Math.Round(1 - ((total - good) / allowed), 9, MidpointRounding.ToEven);

        // A day per row, oldest first, the last being today (so far).
        var today = Times.Day(now);
        var days = Enumerable.Range(0, WindowDays).Select(n => today.AddDays(-(WindowDays - 1 - n))).ToList();
        var perDay = days.ToDictionary(d => d, _ => new long[2]);
        foreach (var p in inside)
        {
            if (perDay.TryGetValue(Times.Day(p.When), out var slot))
            {
                slot[0] += p.Total - p.Good;
                slot[1] += p.Total;
            }
        }

        var burndown = new List<object?[]>();
        long spent = 0;
        foreach (var d in days)
        {
            var bad = perDay[d][0];
            var valid = perDay[d][1];
            spent += bad;
            double? left = allowed == 0 ? null : Math.Round(100 * (1 - (spent / allowed)), 2, MidpointRounding.ToEven);
            burndown.Add([Times.Iso(d), left, bad, valid]);
        }

        return new Sli(
            objective,
            StatusOf(budgetLeft),
            good,
            total,
            total == 0 ? null : (double)good / total,
            allowed,
            budgetLeft,
            // From the hour, so the HTTP rows' hourly bin at the start counts whole.
            Burn(inside, Hour(now.AddDays(-1)), objective.Target),
            Burn(inside, Hour(now.AddDays(-7)), objective.Target),
            burndown);
    }

    /// <summary>
    /// A ratio as a percentage with the decimals a nines target needs (two at
    /// 99% and up), truncated rather than rounded, so a miss never reads as 100%.
    /// </summary>
    public static string FormatRatio(double? ratio)
    {
        if (ratio is not { } r)
        {
            return "–";
        }

        var digits = r >= 0.99 ? 2 : 1;
        var scale = Math.Pow(10, digits);
        var value = Math.Floor((100 * r * scale) + 1e-9) / scale;
        return Fmt.Fixed(value, digits).TrimEnd('0').TrimEnd('.') + "%";
    }

    /// <summary>
    /// How much of a budget is left, or overspent (the caller says which),
    /// truncated towards zero so it never reads as more than it is: one
    /// decimal under 1%, and never 0% unless it is exactly 0.
    /// </summary>
    public static string FormatBudget(double left)
    {
        var value = Math.Abs(100 * left);
        if (value == 0 || value >= 1)
        {
            return $"{Math.Floor(value + 1e-9):0}%";
        }

        return value < 0.1 ? "<0.1%" : $"{Fmt.Fixed(Math.Floor((value * 10) + 1e-9) / 10, 1)}%";
    }

    public static string Worst(IEnumerable<string> statuses)
    {
        var present = statuses.ToHashSet(StringComparer.Ordinal);
        return Statuses.FirstOrDefault(present.Contains) ?? "no-data";
    }

    /// <summary>How many apps are in each status, for the home tile.</summary>
    public static Dictionary<string, int> Tally(IEnumerable<SloReport> reports)
    {
        var tally = Statuses.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        foreach (var report in reports)
        {
            tally[report.Status]++;
        }

        return tally;
    }
}
