using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;

namespace Obsview;

/// <summary>
/// One result row: column name to value. The SDK gives times as
/// <see cref="DateTimeOffset"/>, counts as <see cref="long"/> or
/// <see cref="int"/> and money as <see cref="double"/> or
/// <see cref="decimal"/>; the demo and tests pass the same kinds (or ISO
/// strings for times), and every reader goes through <see cref="RowValues"/>.
/// </summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row()
        : base(StringComparer.Ordinal)
    {
    }

    public object? Get(string column) => TryGetValue(column, out var value) ? value : null;

    /// <summary>The column as text: empty for a null or missing value.</summary>
    public string Text(string column) => Get(column)?.ToString() ?? "";

    /// <summary>The column as text, or <paramref name="fallback"/> when null, missing or empty.</summary>
    public string Text(string column, string fallback) =>
        Text(column) is { Length: > 0 } text ? text : fallback;
}

/// <summary>Numbers from rows, whichever numeric type the SDK chose.</summary>
public static class RowValues
{
    /// <summary>The value as a double, or null when it isn't a number (a string included).</summary>
    public static double? Number(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => null,
    };

    /// <summary>The value as an integer, 0 when it isn't a number.</summary>
    public static long Whole(object? value) => Number(value) is { } n ? (long)n : 0;
}

/// <summary>The workspace refused or failed a query. Shown on the page, not a 500.</summary>
public sealed class QueryFailedException : Exception
{
    public QueryFailedException()
    {
    }

    public QueryFailedException(string message)
        : base(message)
    {
    }

    public QueryFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Somewhere rows come from: the workspace, the cache in front of it, or
/// the demo. Each view runs one query for raw rows and computes every tile
/// and chart from them (see <see cref="Deployments"/>), so the aggregation is
/// plain code with plain tests rather than KQL only the workspace can check.
/// The window is the query's timespan, never interpolated into the KQL.
/// </summary>
public interface ILogsSource
{
    Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default);
}

/// <summary>Log Analytics through <c>Azure.Monitor.Query.Logs</c>, as the app's identity.</summary>
public sealed partial class AzureLogs : ILogsSource
{
    private readonly string _workspaceId;
    private readonly LogsQueryClient _client;
    private readonly ILogger<AzureLogs> _logger;

    public AzureLogs(string workspaceId, string clientId, ILogger<AzureLogs> logger)
    {
        _workspaceId = workspaceId;
        _logger = logger;
        // The user-assigned identity in Azure; your `az login` locally.
        TokenCredential credential = string.IsNullOrEmpty(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
        _client = new LogsQueryClient(credential);
    }

    public async Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
    {
        var result = await Guard(
            async () => (await _client.QueryWorkspaceAsync(_workspaceId, kql, new LogsQueryTimeRange(timespan), cancellationToken: cancellationToken).ConfigureAwait(false)).Value,
            LogQueryFailed,
            cancellationToken).ConfigureAwait(false);

        if (result.Status != LogsQueryResultStatus.Success)
        {
            // A partial result means the workspace cut the query short, and a
            // dashboard built on part of the rows would quietly undercount.
            throw new QueryFailedException("Log Analytics returned a partial result");
        }

        var rows = new List<Row>();
        foreach (var table in result.AllTables)
        {
            foreach (var source in table.Rows)
            {
                var row = new Row();
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    row[table.Columns[i].Name] = source[i];
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// Any failure of the call is a <see cref="QueryFailedException"/>, which
    /// each page renders around, except the caller's own cancellation (a
    /// request aborted, the host stopping). Decided by the token, not the
    /// exception's type: Azure.Core reports its own network timeout as a
    /// <see cref="TaskCanceledException"/>, and letting that through would
    /// turn a slow workspace into a 500, or stop the host from the warm-up.
    /// </summary>
    internal static async Task<T> Guard<T>(Func<Task<T>> call, Action<Exception> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exc) when (!cancellationToken.IsCancellationRequested)
        {
            log(exc);
            throw new QueryFailedException($"Log Analytics query failed: {exc.GetType().Name}", exc);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Log Analytics query failed")]
    private partial void LogQueryFailed(Exception exc);
}

/// <summary>
/// A source that remembers each (query, timespan) for <c>ttl</c>. One lock
/// per key, so two requests for the same cold page wait on one query rather
/// than both running it. A failure is not cached: the next request tries again.
/// </summary>
public sealed class CachedLogs : ILogsSource, IDisposable
{
    private readonly ILogsSource _source;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string, TimeSpan), (long At, IReadOnlyList<Row> Rows)> _entries = new();
    private readonly ConcurrentDictionary<(string, TimeSpan), SemaphoreSlim> _locks = new();

    public CachedLogs(ILogsSource source, TimeSpan ttl, TimeProvider? clock = null)
    {
        _source = source;
        _ttl = ttl;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
    {
        var key = (kql, timespan);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(key, out var hit) && _clock.GetElapsedTime(hit.At) < _ttl)
            {
                return hit.Rows;
            }

            var rows = await _source.QueryAsync(kql, timespan, cancellationToken).ConfigureAwait(false);
            _entries[key] = (_clock.GetTimestamp(), rows);
            return rows;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
    }
}
