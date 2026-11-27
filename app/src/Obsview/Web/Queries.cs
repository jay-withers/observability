using System.Text.Json;
using System.Text.Json.Serialization;

namespace Obsview.Web;

/// <summary>A query's rows, or why it failed: each page decides which failures it renders around.</summary>
public sealed record Result(IReadOnlyList<Row>? Rows, QueryFailedException? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// The queries every page runs, and running them. One set of queries and
/// windows for every page (<see cref="AppQueries"/>), so the apps index, each
/// app and the platform read the same cache entries at a range.
/// </summary>
public sealed partial class Queries(ILogsSource source, Settings settings, ILogger<Queries> logger)
{
    /// <summary>The ranges on offer. Anything else in <c>?days=</c> falls back to the default.</summary>
    public static readonly IReadOnlyList<int> Ranges = [1, 3, 5, 7, 30, 60, 90];

    public const int DefaultRange = 7;

    /// <summary>The JSON the chart payload is written in: the names app.js reads.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>?days=</c> as one of <see cref="Ranges"/>. Read as text, so
    /// <c>?days=30d</c> or an empty value falls back to the default, not an error:
    /// it is a bookmark, not an API.
    /// </summary>
    public static int Range(string? raw) =>
        int.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var days) && Ranges.Contains(days)
            ? days
            : DefaultRange;

    /// <summary>
    /// /readyz's query: the default page's deploy query, which the start-up
    /// warm-up has usually already cached. The shared smoke test calls
    /// /readyz once, with no retry, so it should land on the cache rather than
    /// a cold query; a cold one still proves the identity can read the table.
    /// </summary>
    public (string Kql, TimeSpan Window) ReadyQuery() =>
        (Deployments.Kql(settings.Environment), Deployments.Window(DefaultRange));

    public Dictionary<string, (string Kql, TimeSpan Window)> AppQueries(int span, DateTimeOffset now)
    {
        var env = settings.Environment;
        return new Dictionary<string, (string, TimeSpan)>(StringComparer.Ordinal)
        {
            ["deploys"] = (Deployments.Kql(env), Deployments.Window(span)),
            ["ops"] = (Ops.Kql(env), Ops.Window(span)),
            ["failed"] = (Ops.FailedRunsKql, TimeSpan.FromDays(span)),
            ["costs"] = (Costs.Kql, Costs.Window(span)),
            ["errors"] = (Errors.Kql, Errors.Window(Deployments.RangeStart(span, now), now)),
        };
    }

    /// <summary>The apps index's: every query but failed runs, which it doesn't show.</summary>
    public Dictionary<string, (string Kql, TimeSpan Window)> IndexQueries(int span, DateTimeOffset now)
    {
        var specs = AppQueries(span, now);
        specs.Remove("failed");
        return specs;
    }

    /// <summary>
    /// The SLO pages' own queries, over the fixed SLO window whatever the
    /// range. Deploys come from the pages' deploy query, whose window is never
    /// shorter than 28 days (<see cref="Deployments.Window"/>).
    /// </summary>
    public static Dictionary<string, (string Kql, TimeSpan Window)> SloQueries() => new(StringComparer.Ordinal)
    {
        ["http"] = (Slo.HttpKql, Slo.Window),
        ["jobs"] = (Slo.JobsKql, Slo.Window),
    };

    /// <summary>The home page reads the apps index's queries and the SLOs', so the warm-up covers all three pages.</summary>
    public Dictionary<string, (string Kql, TimeSpan Window)> HomeQueries(int span, DateTimeOffset now)
    {
        var specs = IndexQueries(span, now);
        foreach (var (key, spec) in SloQueries())
        {
            specs[key] = spec;
        }

        return specs;
    }

    /// <summary>The SLO queries, with the deploy query at the page's range so it shares the cache with the other pages.</summary>
    public Dictionary<string, (string Kql, TimeSpan Window)> SloPageQueries(int span)
    {
        var specs = SloQueries();
        specs["deploys"] = (Deployments.Kql(settings.Environment), Deployments.Window(span));
        return specs;
    }

    /// <summary>
    /// Every query at once, each result or its failure, so a cold page waits
    /// for the slowest rather than the sum.
    /// </summary>
    public async Task<Dictionary<string, Result>> GatherAsync(IReadOnlyDictionary<string, (string Kql, TimeSpan Window)> specs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var jobs = specs.ToDictionary(kv => kv.Key, kv => RunAsync(kv.Value.Kql, kv.Value.Window, cancellationToken));
        await Task.WhenAll(jobs.Values).ConfigureAwait(false);
        return jobs.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
    }

    private async Task<Result> RunAsync(string kql, TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            return new Result(await source.QueryAsync(kql, window, cancellationToken).ConfigureAwait(false), null);
        }
        catch (QueryFailedException exc)
        {
            return new Result(null, exc);
        }
    }

    /// <summary>
    /// A secondary query's rows, or null (logged) if it failed: its card says
    /// so and the rest of the page renders, as Grafana failed one panel, not all.
    /// </summary>
    public IReadOnlyList<Row>? Ok(Result result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Failure is { } failure)
        {
            LogSecondaryFailed(what, failure.Message);
            return null;
        }

        return result.Rows;
    }

    /// <summary>Fill the cache for the home page, /apps and /slo at the default range, so the first open after a start is fast.</summary>
    public async Task WarmAsync(CancellationToken cancellationToken = default)
    {
        var got = await GatherAsync(HomeQueries(DefaultRange, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        foreach (var (key, result) in got)
        {
            if (result.Failed)
            {
                LogWarmFailed(key);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{What} query failed; rendering without it: {Reason}")]
    private partial void LogSecondaryFailed(string what, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "cache warm-up of {Key} failed; the first page load will retry")]
    private partial void LogWarmFailed(string key);
}

/// <summary>Warms the cache in the background at start-up: a slow workspace must not hold up the first probe.</summary>
public sealed class Warmer(Queries queries, Settings settings) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        settings.UseDemo ? Task.CompletedTask : queries.WarmAsync(stoppingToken);
}
