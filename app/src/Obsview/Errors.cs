using System.Text.RegularExpressions;

namespace Obsview;

public sealed record ErrorLine(DateTimeOffset When, string Workload, string Message);

/// <summary>One kind of error from one workload: how often, first and last seen, and a sample.</summary>
public sealed class ErrorGroup(string workload, string signature, string sample, int count, DateTimeOffset first, DateTimeOffset last)
{
    public string Workload { get; } = workload;

    public string Signature { get; } = signature;

    public string Sample { get; set; } = sample;

    public int Count { get; set; } = count;

    public DateTimeOffset First { get; set; } = first;

    public DateTimeOffset Last { get; set; } = last;
}

/// <summary>
/// App errors, from Container Apps' console logs. Every app and job writes
/// stdout and stderr to <c>ContainerAppConsoleLogs_CL</c>. A line is an error
/// when it carries an <c>ERROR</c> or <c>CRITICAL</c> level, or is an
/// access-log line with a 5xx status. A traceback's own lines are left out:
/// the <c>ERROR</c> line before it names the error, and each frame is a row
/// of its own.
///
/// The query returns each workload's newest lines raw, capped per workload so
/// one crash loop can't crowd every other app out, with the workload's true
/// total alongside. Grouping is done here, so the rule for "the same error"
/// is tested: the first line with UUIDs, hex ids, numbers and quoted values
/// masked. Read through a fuzzy union with an empty datatable, as the
/// failed-runs query is, so a workspace with no console logs yet answers with
/// no rows. Job rows name the job in <c>ContainerJobName_s</c>, read with
/// <c>column_ifexists</c> in case the table has no job rows yet.
/// </summary>
public static partial class Errors
{
    /// <summary>
    /// The most lines fetched per workload: a crash loop logs the same error
    /// thousands of times. Written into the KQL as a literal; a test keeps
    /// the two in step.
    /// </summary>
    public const int PerWorkload = 300;

    public const string Kql = """
        let lines = union isfuzzy=true ContainerAppConsoleLogs_CL,
            (datatable(TimeGenerated: datetime, ContainerAppName_s: string, Log_s: string)[])
        | extend Workload = coalesce(column_ifexists("ContainerJobName_s", ""), ContainerAppName_s)
        | where isnotempty(Workload)
        | where Log_s has_cs "ERROR" or Log_s has_cs "CRITICAL"
            or Log_s matches regex @'HTTP/[0-9.]+" 5[0-9][0-9]';
        lines
        | summarize Total = count() by Workload
        | join kind=inner (
            lines
            | sort by Workload asc, TimeGenerated desc
            | extend Rank = row_number(1, prev(Workload) != Workload)
            | where Rank <= 300
          ) on Workload
        | project TimeGenerated, Workload, Log_s, Total
        """;

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\b(?=[0-9a-f]*\d)[0-9a-f]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hex();

    // ASCII digits only, as Python's \d over the masked lines in practice reads.
    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex("'[^']*'|\"[^\"]*\"")]
    private static partial Regex Quoted();

    /// <summary>A leading timestamp, as many loggers write one.</summary>
    [GeneratedRegex(@"^\s*\[?\d{4}-\d\d-\d\d[T ][\d:.,]+Z?\]?\s*")]
    private static partial Regex Stamp();

    /// <summary>Access-log lines are grouped by method, path and status only.</summary>
    [GeneratedRegex("\"(?<method>[A-Z]+) (?<path>[^ ?\"]*)[^\"]*\" (?<status>5\\d\\d)")]
    private static partial Regex Access();

    /// <summary>
    /// Back to <paramref name="since"/>, the range's first midnight as the
    /// other views count it, rounded up to five minutes so the cache key holds
    /// still between loads. Lines before <paramref name="since"/> in that
    /// margin are dropped by the caller.
    /// </summary>
    public static TimeSpan Window(DateTimeOffset since, DateTimeOffset now)
    {
        const double step = 300;
        var seconds = (now - since).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Ceiling(seconds / step) * step);
    }

    /// <summary>UUIDs first, so their hex segments aren't masked piecemeal.</summary>
    private static string MaskIds(string text) =>
        Digits().Replace(Hex().Replace(Uuid().Replace(text, "…"), "…"), "N");

    /// <summary>Each workload's true count of error lines, past the per-workload cap.</summary>
    public static Dictionary<string, int> Totals(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var workload = row.Text("Workload");
            totals[workload] = RowValues.Number(row.Get("Total")) is { } total
                ? (int)total
                : totals.GetValueOrDefault(workload) + 1;
        }

        return totals;
    }

    public static List<ErrorLine> Parse(IEnumerable<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var lines = new List<ErrorLine>();
        foreach (var row in rows)
        {
            if (Times.Parse(row.Get("TimeGenerated")) is not { } when)
            {
                continue;
            }

            lines.Add(new ErrorLine(when, row.Text("Workload"), row.Text("Log_s").Trim()));
        }

        return lines;
    }

    /// <summary>What makes two lines the same error: the first line, masked.</summary>
    public static string Signature(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var first = message.Length > 0 ? message.Split('\n')[0].TrimEnd('\r') : "";
        first = Stamp().Replace(first, "", 1);
        var access = Access().Match(first);
        if (access.Success)
        {
            return $"{access.Groups["method"].Value} {MaskIds(access.Groups["path"].Value)} → {access.Groups["status"].Value}";
        }

        var masked = MaskIds(Quoted().Replace(first, "…"));
        return masked.Length > 200 ? masked[..200] : masked;
    }

    /// <summary>One group per workload and signature, the most recent first.</summary>
    public static List<ErrorGroup> Group(IEnumerable<ErrorLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var groups = new Dictionary<(string, string), ErrorGroup>();
        foreach (var line in lines)
        {
            var signature = Signature(line.Message);
            var sample = line.Message.Length > 300 ? line.Message[..300] : line.Message;
            if (!groups.TryGetValue((line.Workload, signature), out var group))
            {
                groups[(line.Workload, signature)] = new ErrorGroup(line.Workload, signature, sample, 1, line.When, line.When);
                continue;
            }

            group.Count++;
            if (line.When > group.Last)
            {
                group.Last = line.When;
                group.Sample = sample;
            }

            if (line.When < group.First)
            {
                group.First = line.When;
            }
        }

        return [.. groups.Values.OrderByDescending(g => g.Last)];
    }
}
