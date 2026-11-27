using System.Globalization;

namespace Obsview;

/// <summary>One resource's spend on one service on one day, in one currency.</summary>
public sealed record Cost(DateOnly Day, string ResourceGroup, string Service, double Amount, string Currency, string ResourceId = "")
{
    public string Area => Costs.AreaOf(ResourceGroup);

    /// <summary>The resource's name: its ID's last segment.</summary>
    public string Resource
    {
        get
        {
            var id = ResourceId.TrimEnd('/');
            var name = id[(id.LastIndexOf('/') + 1)..];
            return name.Length > 0 ? name : Costs.Unattributed;
        }
    }
}

/// <summary>A horizontal bar: its label, value, width (0-100) and chart colour (1-based, 0 for plain).</summary>
public sealed record Bar(string Label, double Value, double Width, int Colour = 0);

public sealed record CostsView(
    int Days,
    DateTimeOffset Now,
    string Symbol,
    string MonthLabel,
    double MonthToDate,
    double? Forecast,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    double Total,
    double? PriorTotal,
    string? Delta,
    string Tone,
    double Budget,
    double BudgetUsed,
    DateOnly? DataThrough,
    int LeftOut,
    IReadOnlyList<Bar> Groups,
    IReadOnlyList<Bar> Services,
    IReadOnlyList<Bar> Resources,
    IReadOnlyList<Bar> Areas,
    CostCharts Charts)
{
    /// <summary>A value as a percentage of the gauge's track.</summary>
    public double Scale(double value)
    {
        var top = Math.Max(Math.Max(Budget, Forecast ?? 0.0), MonthToDate) * 1.05;
        return top == 0 ? 0.0 : 100.0 * value / top;
    }
}

/// <summary>
/// The daily chart's series (<c>series_keys</c>, in colour order) and the
/// treemap. <c>by_area</c> tells the page the treemap's top level is the
/// areas, coloured as the chart's; a slice's is its resource groups.
/// </summary>
public sealed record CostCharts(DailySpend Spend, IReadOnlyList<TreeNode> Treemap, IReadOnlyList<string> SeriesKeys, bool ByArea);

public sealed record DailySpend(IReadOnlyList<string> Dates, IReadOnlyDictionary<string, IReadOnlyList<double>> Series);

/// <summary>A treemap node: a leaf has a value, a branch children.</summary>
public sealed record TreeNode(string Name, double? Value = null, IReadOnlyList<TreeNode>? Children = null);

/// <summary>
/// The costs panels, from <c>Costs_CL</c>. <c>schedule-costs</c> posts the
/// last seven days per resource and service every day, because Azure revises
/// a day for about three. So a day is posted by several runs, and the latest
/// run wins: the KQL keeps each UsageDate's rows from the run that posted it
/// last (one <c>TimeGenerated</c> per run) and sums them per resource and
/// service. Not <c>arg_max</c> per key: a run that no longer has a row for a
/// key (it fell to zero) would leave the older run's row standing, and rows
/// from before <c>ResourceId</c> was sent would double the newer ones.
/// Figures are ActualCost, before any credit.
///
/// <b>Areas are fixed groups of resource groups</b>, so each keeps its colour
/// whatever the month's ranking. A new resource group falls into "landing
/// zone and other" until it is added to <see cref="Areas"/>.
/// </summary>
public static class Costs
{
    public static readonly IReadOnlyDictionary<string, string> Areas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rg-platform-dev"] = "platform",
        ["rg-gymlog-dev"] = "apps",
        ["rg-career-dev"] = "apps",
        ["rg-finances-dev"] = "apps",
        ["rg-sectest-dev"] = "apps",
        ["rg-repoagent-dev"] = "apps",
        ["rg-observability-dev"] = "apps",
        ["rg-marketagent-dev"] = "market-agent",
        ["rg-aks-dev"] = "aks",
    };

    public const string Other = "landing zone and other";

    /// <summary>The order areas are stacked and coloured in: never the month's ranking.</summary>
    public static readonly IReadOnlyList<string> AreaOrder = ["platform", "apps", "market-agent", "aks", Other];

    /// <summary>
    /// The series a slice's daily chart stacks: its top services, the rest
    /// folded into one, within the five chart colours.
    /// </summary>
    public const int ServiceSeries = 5;

    public const string Rest = "everything else";

    /// <summary>Rows from before the feed sent a resource: the group and service only.</summary>
    public const string Unattributed = "(not broken down)";

    public const string Kql = """
        let runs = Costs_CL | summarize Run = max(TimeGenerated) by UsageDate;
        Costs_CL
        | join kind=inner runs on UsageDate
        | where TimeGenerated == Run
        | extend ResourceId = column_ifexists("ResourceId", "")
        | summarize Cost = sum(Cost) by UsageDate, ResourceGroup, ServiceName, ResourceId, Currency
        | project UsageDate, ResourceGroup, ServiceName, ResourceId, Cost, Currency
        """;

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["USD"] = "$",
        ["GBP"] = "£",
        ["EUR"] = "€",
    };

    public static string AreaOf(string resourceGroup) =>
        Areas.GetValueOrDefault(resourceGroup.ToLowerInvariant(), Other);

    /// <summary>
    /// The range and the one before it (for the delta), or this month,
    /// whichever is longer, plus the week a day's rows keep being re-posted
    /// for, so the latest post of each day is in it.
    /// </summary>
    public static TimeSpan Window(int days) => TimeSpan.FromDays(Math.Max(2 * days, 31) + 8);

    /// <summary>The currency most rows are in (alphabetically first on a tie). Summing two would be meaningless.</summary>
    public static string BillingCurrency(IEnumerable<Cost> costs) =>
        costs.GroupBy(c => c.Currency)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";

    public static string Symbol(string currency) =>
        Symbols.TryGetValue(currency, out var symbol) ? symbol : currency.Length > 0 ? currency + " " : "";

    /// <summary>The range's total in the billing currency, as <see cref="Build"/> counts it.</summary>
    public static double Spend(IReadOnlyList<Cost> costs, DateOnly end, int days)
    {
        var currency = BillingCurrency(costs);
        var start = end.AddDays(-(days - 1));
        return costs.Where(c => c.Currency == currency && start <= c.Day && c.Day <= end).Sum(c => c.Amount);
    }

    /// <summary>
    /// The newest day with data, never after today. The feed lags a day or
    /// two, so a range ending today would end in days that read as no spend.
    /// </summary>
    public static DateOnly LastDay(IReadOnlyList<Cost> costs, DateOnly today)
    {
        var newest = costs.Count == 0 ? today : costs.Max(c => c.Day);
        return newest < today ? newest : today;
    }

    public static List<Cost> Parse(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var costs = new List<Cost>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("UsageDate")) is not { } when || RowValues.Number(row.Get("Cost")) is not { } value)
            {
                continue;
            }

            costs.Add(new Cost(
                Times.Day(when),
                row.Text("ResourceGroup", "(none)").ToLowerInvariant(),
                row.Text("ServiceName", "(none)"),
                value,
                row.Text("Currency"),
                row.Text("ResourceId").ToLowerInvariant()));
        }

        return costs;
    }

    /// <summary>
    /// <paramref name="end"/> is the range's last day; by default the last
    /// day with data. An app's page passes the estate's, so a quiet app's
    /// range isn't shifted. <paramref name="byArea"/> is the whole estate's
    /// view: the daily chart stacks the fixed areas and the treemap starts at
    /// them. A slice (one app, the platform) passes false: its chart stacks
    /// its own top services and its treemap starts at its resource groups, so
    /// nothing on it names another area.
    /// </summary>
    public static CostsView Build(IReadOnlyList<Cost> all, int days, DateTimeOffset now, double budget, DateOnly? end = null, bool byArea = true)
    {
        ArgumentNullException.ThrowIfNull(all);
        var today = Times.Day(now);
        // One currency: the one most rows are in; any others are counted and left out.
        var currency = BillingCurrency(all);
        var leftOut = all.Count(c => c.Currency != currency);
        var costs = all.Where(c => c.Currency == currency).ToList();

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var month = costs.Where(c => monthStart <= c.Day && c.Day <= today).ToList();
        var mtd = month.Sum(c => c.Amount);

        // Grafana's forecast: the month's spend so far, per day of data, times
        // the days in the month. Days of data, not days elapsed: the feed lags
        // a day or two, and dividing by today's date would understate it.
        double? forecast = null;
        if (month.Count > 0)
        {
            var through = month.Max(c => c.Day);
            var daysWithData = through.DayNumber - monthStart.DayNumber + 1;
            forecast = mtd / daysWithData * DateTime.DaysInMonth(today.Year, today.Month);
        }

        var symbol = Symbol(currency);
        var last = end ?? LastDay(costs, today);
        var start = last.AddDays(-(days - 1));
        var priorStart = start.AddDays(-days);
        var inRange = costs.Where(c => start <= c.Day && c.Day <= last).ToList();
        var total = inRange.Sum(c => c.Amount);

        // A prior total only when the data reaches back that far: a range
        // before the table's retention would read as a fall from zero.
        double? priorTotal = null;
        if (costs.Count > 0 && costs.Min(c => c.Day) <= priorStart)
        {
            priorTotal = costs.Where(c => priorStart <= c.Day && c.Day < start).Sum(c => c.Amount);
        }

        string? delta = null;
        var tone = "neutral";
        if (priorTotal is { } prior)
        {
            var diff = total - prior;
            delta = $"{(diff >= 0 ? "+" : "-")}{symbol}{Fmt.Grouped(Math.Abs(diff), 2)} vs prior {days}d";
            if (Math.Round(diff, 2) != 0)
            {
                tone = diff < 0 ? "good" : "bad";
            }
        }

        var byGroup = new Dictionary<string, double>(StringComparer.Ordinal);
        var byService = new Dictionary<string, double>(StringComparer.Ordinal);
        var byAreaTotals = new Dictionary<string, double>(StringComparer.Ordinal);
        // Keyed by ID, labelled by name: names are unique only within a group.
        var byResource = new Dictionary<string, double>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in inRange)
        {
            Add(byGroup, c.ResourceGroup, c.Amount);
            Add(byService, c.Service, c.Amount);
            Add(byAreaTotals, c.Area, c.Amount);
            var key = c.ResourceId.Length > 0 ? c.ResourceId : c.ResourceGroup + "/";
            Add(byResource, key, c.Amount);
            names[key] = $"{c.Resource} · {c.ResourceGroup}";
        }

        DateOnly? dataThrough = costs.Any(c => c.Day <= today) ? costs.Where(c => c.Day <= today).Max(c => c.Day) : null;
        return new CostsView(
            days,
            now,
            symbol,
            today.ToString("MMMM", CultureInfo.InvariantCulture),
            mtd,
            forecast,
            start,
            last,
            total,
            priorTotal,
            delta,
            tone,
            budget,
            budget == 0 ? 0.0 : 100.0 * mtd / budget,
            // The newest day with any data: early in a month the feed's lag
            // leaves this month empty, and the page should say "through 31 Oct"
            // rather than look broken.
            dataThrough,
            leftOut,
            Bars(byGroup, colour: byArea ? g => AreaColour(AreaOf(g)) : null),
            Bars(byService, limit: 8),
            [.. Bars(byResource, limit: 12).Select(b => b with { Label = b.Label == Rest ? Rest : names[b.Label] })],
            Bars(byAreaTotals, colour: AreaColour),
            Charts(costs, inRange, last, days, byArea));
    }

    private static void Add(Dictionary<string, double> totals, string key, double amount) =>
        totals[key] = totals.GetValueOrDefault(key) + amount;

    private static int AreaColour(string name) => AreaOrder.ToList().IndexOf(name) + 1;

    private static CostCharts Charts(List<Cost> costs, List<Cost> inRange, DateOnly end, int days, bool byArea)
    {
        if (byArea)
        {
            List<string> areaKeys = [.. AreaOrder];
            return new CostCharts(Daily(costs, end, days, areaKeys, c => c.Area), Treemap(inRange, AreaOrder, c => c.Area), areaKeys, true);
        }

        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in inRange)
        {
            Add(totals, c.Service, c.Amount);
        }

        // Every service with a row, even one netting to zero or less (a
        // credit), so the stacked days still add up to the range's total.
        var ranked = totals.OrderBy(kv => -kv.Value).Select(kv => kv.Key).ToList();
        var top = ranked.Count <= ServiceSeries ? ranked : ranked.Take(ServiceSeries - 1).ToList();
        var keys = new List<string>(top);
        if (ranked.Count > top.Count)
        {
            keys.Add(Rest);
        }

        var chosen = top.ToHashSet(StringComparer.Ordinal);
        var groups = inRange.Select(c => c.ResourceGroup).Distinct().Order(StringComparer.Ordinal).ToList();
        return new CostCharts(
            Daily(costs, end, days, keys, c => chosen.Contains(c.Service) ? c.Service : Rest),
            Treemap(inRange, groups, c => c.ResourceGroup, nested: false),
            keys,
            false);
    }

    private static List<Bar> Bars(Dictionary<string, double> totals, int limit = 0, Func<string, int>? colour = null)
    {
        // Stable: equal totals keep their first-seen order, as Python's sort does.
        var ranked = totals.OrderBy(kv => -kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        if (limit > 0 && ranked.Count > limit)
        {
            var rest = ranked.Skip(limit - 1).Sum(kv => kv.Value);
            ranked = [.. ranked.Take(limit - 1), (Rest, rest)];
        }

        var topValue = ranked.Count == 0 ? 0.0 : ranked.Max(kv => kv.Value);
        return [.. ranked
            .Where(kv => kv.Value > 0)
            .Select(kv => new Bar(kv.Key, kv.Value, topValue == 0 ? 0.0 : 100.0 * kv.Value / topValue, colour?.Invoke(kv.Key) ?? 0))];
    }

    private static DailySpend Daily(List<Cost> costs, DateOnly end, int days, List<string> keys, Func<Cost, string> key)
    {
        var dates = Enumerable.Range(0, days).Select(i => end.AddDays(-(days - 1 - i))).ToList();
        var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var series = keys.ToDictionary(k => k, _ => new double[dates.Count], StringComparer.Ordinal);
        foreach (var c in costs)
        {
            if (index.TryGetValue(c.Day, out var i) && series.TryGetValue(key(c), out var values))
            {
                values[i] += c.Amount;
            }
        }

        return new DailySpend(
            [.. dates.Select(Times.Iso)],
            keys.ToDictionary(k => k, k => (IReadOnlyList<double>)[.. series[k].Select(v => Math.Round(v, 2, MidpointRounding.ToEven))], StringComparer.Ordinal));
    }

    /// <summary>
    /// Over the range: <paramref name="key"/> (an area) &gt; resource group
    /// &gt; resource &gt; service, or, not nested, resource group &gt;
    /// resource &gt; service. Resources are keyed by ID (names are unique only
    /// per type) and labelled by name.
    /// </summary>
    private static List<TreeNode> Treemap(List<Cost> inRange, IEnumerable<string> order, Func<Cost, string> key, bool nested = true)
    {
        var tree = new Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in inRange)
        {
            var top = Child(tree, key(c));
            var group = Child(top, c.ResourceGroup);
            var resource = Child(group, c.ResourceId);
            Add(resource, c.Service, c.Amount);
            names[c.ResourceId] = c.Resource;
        }

        List<TreeNode> Services(Dictionary<string, double> totals) =>
            [.. totals.OrderBy(kv => -kv.Value).Where(kv => kv.Value > 0).Select(kv => new TreeNode(kv.Key, Math.Round(kv.Value, 2, MidpointRounding.ToEven)))];

        List<TreeNode> Resources(Dictionary<string, Dictionary<string, double>> group) =>
            [.. group.OrderBy(kv => -kv.Value.Values.Sum())
                .Select(kv => new TreeNode(names[kv.Key], Children: Services(kv.Value)))
                .Where(n => n.Children!.Count > 0)];

        var nodes = new List<TreeNode>();
        foreach (var top in order)
        {
            if (!tree.TryGetValue(top, out var byGroup))
            {
                continue;
            }

            var groups = byGroup
                .OrderBy(kv => -kv.Value.Values.Sum(services => services.Values.Sum()))
                .Select(kv => new TreeNode(kv.Key, Children: Resources(kv.Value)))
                .Where(g => g.Children!.Count > 0)
                .ToList();
            if (!nested)
            {
                nodes.AddRange(groups);
            }
            else if (groups.Count > 0)
            {
                nodes.Add(new TreeNode(top, Children: groups));
            }
        }

        return nodes;
    }

    private static TValue Child<TValue>(Dictionary<string, TValue> parent, string key)
        where TValue : new()
    {
        if (!parent.TryGetValue(key, out var child))
        {
            child = new TValue();
            parent[key] = child;
        }

        return child;
    }
}
