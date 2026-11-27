namespace Obsview;

/// <summary>One row of <c>Deployments_CL</c>: a deploy, as container-apps-deploy.yml wrote it.</summary>
public sealed record Deploy(
    DateTimeOffset When,
    string Repo,
    string App,
    string Version,
    string PreviousVersion,
    string Outcome,
    string Trigger,
    double? LeadTimeSec,
    string Commit,
    string RunUrl)
{
    public bool Succeeded => Deployments.Succeeded.Contains(Outcome);

    /// <summary><c>repo</c> alone where the app is named after it, else <c>repo / app</c>.</summary>
    public string Label => App.Length == 0 || App == Repo ? Repo : $"{Repo} / {App}";
}

/// <summary>A headline tile: its figure, sparkline and the delta against the prior range.</summary>
public sealed record Kpi(string Label, string Value, string Spark = "", string? Delta = null, string Tone = "neutral");

/// <summary>One app's line in "By app".</summary>
public sealed record AppRow(
    string Label,
    string Repo,
    int Deploys,
    double SuccessPct,
    int RolledBack,
    int Failed,
    string MedianLead,
    Deploy Last);

public sealed record DeploymentsView(
    int Days,
    DateTimeOffset Now,
    int Total,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyList<AppRow> Apps,
    IReadOnlyList<Deploy> Recent,
    DeploymentCharts Charts);

/// <summary>The deploy charts' data, as app.js reads it (names are the JSON's).</summary>
public sealed record DeploymentCharts(
    string Start,
    IReadOnlyList<string> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Outcomes,
    IReadOnlyList<NamedCount> Triggers,
    Calendar Calendar,
    Timeline Timeline,
    LeadHistogram Lead);

public sealed record NamedCount(string Name, int Value);

public sealed record Calendar(string Start, string End, IReadOnlyList<object[]> Data);

/// <summary>One lane per app; each point is [time, lane, outcome, version, trigger].</summary>
public sealed record Timeline(IReadOnlyList<string> Apps, IReadOnlyList<object[]> Points);

public sealed record LeadHistogram(IReadOnlyList<string> Labels, IReadOnlyList<int> Counts);

/// <summary>
/// The deployments panels, from <c>Deployments_CL</c> rows to everything on
/// the page. Everything here is a pure function of those rows and <c>now</c>,
/// so the tests pin the arithmetic that used to live in Grafana panel KQL.
/// "Succeeded" means <c>success</c> or <c>untested</c>, as the Grafana
/// panels counted it: an untested deploy is one with no smoke test, and it
/// did go out.
/// </summary>
public static class Deployments
{
    public static readonly IReadOnlyList<string> Outcomes = ["success", "untested", "rolled_back", "failed"];

    public static readonly IReadOnlySet<string> Succeeded = new HashSet<string>(StringComparer.Ordinal) { "success", "untested" };

    /// <summary>
    /// The calendar always shows this many days, whatever the range: a
    /// heatmap of one week is a single column.
    /// </summary>
    public const int CalendarDays = 91;

    /// <summary>Lead-time buckets, in minutes. The last bucket is open-ended.</summary>
    public static readonly IReadOnlyList<int> LeadBuckets = [5, 10, 20, 40];

    /// <summary>The one query behind the panels. The window is the query's timespan.</summary>
    public static string Kql(string environment) =>
        "Deployments_CL\n"
        + $"| where Environment == \"{environment}\"\n"
        + "| project TimeGenerated, Repo, App, Version, PreviousVersion, Outcome, Trigger,\n"
        + "          LeadTimeSec, CommitSha, RunUrl\n"
        + "| order by TimeGenerated desc\n"
        + "| take 20000";

    /// <summary>
    /// How far back the query reaches: the range, the range before it (for
    /// the deltas), and the calendar, whichever is longest.
    /// </summary>
    public static TimeSpan Window(int days) => TimeSpan.FromDays(Math.Max(2 * days, CalendarDays));

    public static List<Deploy> Parse(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var deploys = new List<Deploy>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("TimeGenerated")) is not { } when)
            {
                continue;
            }

            var commit = row.Text("CommitSha");
            deploys.Add(new Deploy(
                when,
                row.Text("Repo"),
                row.Text("App"),
                row.Text("Version"),
                row.Text("PreviousVersion"),
                row.Text("Outcome", "unknown"),
                row.Text("Trigger", "unknown"),
                RowValues.Number(row.Get("LeadTimeSec")),
                commit.Length > 7 ? commit[..7] : commit,
                row.Text("RunUrl")));
        }

        // Stable, newest first: equal times keep the rows' own order.
        return [.. deploys.OrderByDescending(d => d.When)];
    }

    /// <summary>The range is whole UTC days ending today: its first midnight.</summary>
    public static DateTimeOffset RangeStart(int days, DateTimeOffset now) =>
        Times.Midnight(Times.Day(now).AddDays(-(days - 1)));

    public static DeploymentsView Build(IReadOnlyList<Deploy> deploys, int days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deploys);
        // The range is whole UTC days ending today, so the tiles, the daily
        // bars and the sparklines all count the same deploys. A rolling
        // `now - days` would touch days + 1 dates and leave the oldest partial
        // one out of the charts while the tiles counted it.
        var today = Times.Day(now);
        var dates = Enumerable.Range(0, days).Select(i => today.AddDays(-(days - 1 - i))).ToList();
        var currentStart = RangeStart(days, now);
        var previousStart = currentStart.AddDays(-days);
        var current = deploys.Where(d => currentStart <= d.When && d.When <= now).ToList();
        var previous = deploys.Where(d => previousStart <= d.When && d.When < currentStart).ToList();
        var byDay = current.GroupBy(d => Times.Day(d.When)).ToDictionary(g => g.Key, g => g.ToList());
        var perDay = dates.Select(day => byDay.TryGetValue(day, out var ds) ? ds : []).ToList();

        return new DeploymentsView(
            days,
            now,
            current.Count,
            BuildKpis(current, previous, perDay, days),
            BuildApps(current),
            [.. current.Take(20)],
            new DeploymentCharts(
                Times.JsTime(currentStart),
                [.. dates.Select(Times.Iso)],
                Outcomes.ToDictionary(o => o, o => (IReadOnlyList<int>)[.. perDay.Select(ds => ds.Count(d => d.Outcome == o))]),
                [.. current.GroupBy(d => d.Trigger)
                    .Select((g, i) => (g, i))
                    .OrderByDescending(x => x.g.Count())
                    .ThenBy(x => x.i)
                    .Select(x => new NamedCount(x.g.Key, x.g.Count()))],
                BuildCalendar(deploys, now),
                BuildTimeline(current),
                BuildLeadHistogram(current)));
    }

    // --- tiles ---------------------------------------------------------------

    public static double? Rate(int part, int whole) => whole == 0 ? null : 100.0 * part / whole;

    private static double? MedianLead(IEnumerable<Deploy> deploys)
    {
        var leads = deploys.Where(d => d.LeadTimeSec is not null).Select(d => d.LeadTimeSec!.Value).Order().ToList();
        if (leads.Count == 0)
        {
            return null;
        }

        var mid = leads.Count / 2;
        return leads.Count % 2 == 1 ? leads[mid] : (leads[mid - 1] + leads[mid]) / 2;
    }

    private static List<Kpi> BuildKpis(List<Deploy> current, List<Deploy> previous, List<List<Deploy>> perDay, int days)
    {
        var prior = $"vs prior {days}d";

        (string, string) CountDelta(int nowN, int beforeN, bool? lowerIsBetter)
        {
            var diff = nowN - beforeN;
            var tone = "neutral";
            if (diff != 0 && lowerIsBetter is { } lower)
            {
                tone = (diff < 0) == lower ? "good" : "bad";
            }

            return ($"{Fmt.WithSign(diff)} {prior}", tone);
        }

        (string?, string) PctDelta(double? nowV, double? beforeV, bool? higherIsBetter)
        {
            if (nowV is null || beforeV is null)
            {
                return (null, "neutral");
            }

            var diff = nowV.Value - beforeV.Value;
            var tone = "neutral";
            if (Math.Round(diff, 1) != 0 && higherIsBetter is { } higher)
            {
                tone = (diff > 0) == higher ? "good" : "bad";
            }

            return ($"{Fmt.WithSign(diff, 1)} pts {prior}", tone);
        }

        int Outcome(IEnumerable<Deploy> ds, string outcome) => ds.Count(d => d.Outcome == outcome);

        var nNow = current.Count;
        var nBefore = previous.Count;
        var rateNow = Rate(current.Count(d => d.Succeeded), nNow);
        var rateBefore = Rate(previous.Count(d => d.Succeeded), nBefore);
        var leadNow = MedianLead(current);
        var leadBefore = MedianLead(previous);
        var renoNow = Rate(current.Count(d => d.Trigger == "renovate"), nNow);
        var renoBefore = Rate(previous.Count(d => d.Trigger == "renovate"), nBefore);

        var kpis = new List<Kpi>();
        var (delta, tone) = CountDelta(nNow, nBefore, null);
        kpis.Add(new Kpi("Deploys", nNow.ToString(System.Globalization.CultureInfo.InvariantCulture), Sparkline.Path(perDay.Select(ds => (double)ds.Count)), delta, tone));

        var (pct, pctTone) = PctDelta(rateNow, rateBefore, true);
        kpis.Add(new Kpi(
            "Success rate",
            FormatPct(rateNow),
            Sparkline.Path([.. perDay.Select(ds => Rate(ds.Count(d => d.Succeeded), ds.Count))]),
            pct,
            pctTone));

        foreach (var (label, outcome) in new[] { ("Rolled back", "rolled_back"), ("Failed", "failed") })
        {
            (delta, tone) = CountDelta(Outcome(current, outcome), Outcome(previous, outcome), true);
            kpis.Add(new Kpi(
                label,
                Outcome(current, outcome).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Sparkline.Path(perDay.Select(ds => (double)Outcome(ds, outcome))),
                delta,
                tone));
        }

        string? leadDelta = null;
        var leadTone = "neutral";
        if (leadNow is { } ln && leadBefore is { } lb)
        {
            var diff = ln - lb;
            leadDelta = $"{(diff >= 0 ? "+" : "-")}{FormatDuration(Math.Abs(diff))} {prior}";
            if (Math.Abs(diff) >= 1)
            {
                leadTone = diff < 0 ? "good" : "bad";
            }
        }

        kpis.Add(new Kpi(
            "Median lead time",
            leadNow is { } lead ? FormatDuration(lead) : "–",
            Sparkline.Path([.. perDay.Select(MedianLead)]),
            leadDelta,
            leadTone));

        (pct, pctTone) = PctDelta(renoNow, renoBefore, null);
        kpis.Add(new Kpi(
            "Renovate share",
            FormatPct(renoNow),
            Sparkline.Path([.. perDay.Select(ds => Rate(ds.Count(d => d.Trigger == "renovate"), ds.Count))]),
            pct,
            pctTone));
        return kpis;
    }

    public static string FormatPct(double? value) => value switch
    {
        null => "–",
        0 or 100 => $"{Fmt.Fixed(value.Value, 0)}%",
        _ => $"{Fmt.Fixed(value.Value, 1)}%",
    };

    public static string FormatDuration(double seconds)
    {
        var total = (long)Math.Round(seconds, MidpointRounding.ToEven);
        if (total < 60)
        {
            return $"{total}s";
        }

        var minutes = total / 60;
        var secs = total % 60;
        if (minutes < 60)
        {
            return minutes < 10 && secs != 0 ? $"{minutes}m {secs:00}s" : $"{minutes}m";
        }

        return $"{minutes / 60}h {minutes % 60:00}m";
    }

    public static string FormatAgo(DateTimeOffset when, DateTimeOffset now)
    {
        var seconds = (now - when).TotalSeconds;
        return seconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)(seconds / 60)}m ago",
            < 86400 => $"{(int)(seconds / 3600)}h ago",
            _ => $"{(int)(seconds / 86400)}d ago",
        };
    }

    // --- tables --------------------------------------------------------------

    private static List<AppRow> BuildApps(List<Deploy> current)
    {
        // Newest first, so each group's first is its last deploy.
        return [.. current
            .GroupBy(d => d.Label)
            .Select(g =>
            {
                var ds = g.ToList();
                var lead = MedianLead(ds);
                return new AppRow(
                    g.Key,
                    ds[0].Repo,
                    ds.Count,
                    Rate(ds.Count(d => d.Succeeded), ds.Count) ?? 0.0,
                    ds.Count(d => d.Outcome == "rolled_back"),
                    ds.Count(d => d.Outcome == "failed"),
                    lead is { } l ? FormatDuration(l) : "–",
                    ds[0]);
            })
            .OrderByDescending(r => r.Last.When)];
    }

    // --- charts --------------------------------------------------------------

    private static Calendar BuildCalendar(IReadOnlyList<Deploy> deploys, DateTimeOffset now)
    {
        var end = Times.Day(now);
        var start = end.AddDays(-(CalendarDays - 1));
        var counts = deploys.GroupBy(d => Times.Day(d.When)).ToDictionary(g => g.Key, g => g.Count());
        var data = Enumerable.Range(0, CalendarDays)
            .Select(i => start.AddDays(i))
            .Select(day => new object[] { Times.Iso(day), counts.GetValueOrDefault(day) })
            .ToList();
        return new Calendar(Times.Iso(start), Times.Iso(end), data);
    }

    /// <summary>One lane per app, ordered by deploy count so the busiest sit at the top.</summary>
    private static Timeline BuildTimeline(List<Deploy> current)
    {
        var counts = current.GroupBy(d => d.Label).ToDictionary(g => g.Key, g => g.Count());
        // Reversed: ECharts draws the first category at the bottom.
        var apps = counts
            .OrderBy(kv => -kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .Reverse()
            .ToList();
        var index = apps.Select((label, i) => (label, i)).ToDictionary(x => x.label, x => x.i);
        var points = Enumerable.Reverse(current)
            .Select(d => new object[] { Times.JsTime(d.When), index[d.Label], d.Outcome, d.Version, d.Trigger })
            .ToList();
        return new Timeline(apps, points);
    }

    private static LeadHistogram BuildLeadHistogram(List<Deploy> current)
    {
        var edges = new List<int> { 0 };
        edges.AddRange(LeadBuckets);
        var labels = edges.Zip(edges.Skip(1)).Select(p => $"{p.First}–{p.Second}m").ToList();
        labels.Add($"{LeadBuckets[^1]}m+");
        var counts = new int[labels.Count];
        foreach (var d in current)
        {
            if (d.LeadTimeSec is not { } lead)
            {
                continue;
            }

            var minutes = lead / 60;
            var slot = LeadBuckets.Count;
            for (var i = 0; i < LeadBuckets.Count; i++)
            {
                if (minutes < LeadBuckets[i])
                {
                    slot = i;
                    break;
                }
            }

            counts[slot]++;
        }

        return new LeadHistogram(labels, counts);
    }
}
