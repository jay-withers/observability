using Microsoft.Extensions.Time.Testing;

namespace Obsview.Tests;

public sealed class LogsTests
{
    private sealed class Counting : ILogsSource
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fail)
            {
                throw new QueryFailedException("boom");
            }

            return Task.FromResult<IReadOnlyList<Row>>([R.Of(("kql", kql), ("n", Calls))]);
        }
    }

    [Fact]
    public async Task Cache_serves_repeat_queries_until_the_ttl_passes()
    {
        var source = new Counting();
        var clock = new FakeTimeProvider();
        using var cached = new CachedLogs(source, TimeSpan.FromSeconds(300), clock);
        var span = TimeSpan.FromDays(1);
        Assert.Equal(1, (await cached.QueryAsync("q", span))[0]["n"]);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Equal(1, (await cached.QueryAsync("q", span))[0]["n"]);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await cached.QueryAsync("q", span))[0]["n"]);
    }

    [Fact]
    public async Task Cache_keys_on_timespan_as_well_as_query()
    {
        var source = new Counting();
        using var cached = new CachedLogs(source, TimeSpan.FromSeconds(300), new FakeTimeProvider());
        await cached.QueryAsync("q", TimeSpan.FromDays(1));
        await cached.QueryAsync("q", TimeSpan.FromDays(2));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_failure_is_not_cached()
    {
        var source = new Counting { Fail = true };
        using var cached = new CachedLogs(source, TimeSpan.FromSeconds(300), new FakeTimeProvider());
        await Assert.ThrowsAsync<QueryFailedException>(() => cached.QueryAsync("q", TimeSpan.FromDays(1)));
        source.Fail = false;
        Assert.Equal(2, (await cached.QueryAsync("q", TimeSpan.FromDays(1)))[0]["n"]);
    }

    [Fact]
    public async Task Two_requests_for_a_cold_key_run_one_query()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var source = new Gated(async () =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
        });
        using var cached = new CachedLogs(source, TimeSpan.FromSeconds(300), new FakeTimeProvider());
        var first = cached.QueryAsync("q", TimeSpan.FromDays(1));
        var second = cached.QueryAsync("q", TimeSpan.FromDays(1));
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
    }

    private sealed class Gated(Func<Task> wait) : ILogsSource
    {
        public async Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
        {
            await wait();
            return [];
        }
    }

    [Fact]
    public async Task An_sdk_timeout_is_a_query_failure_not_a_crash()
    {
        // Regression: Azure.Core reports its own network timeout as a
        // TaskCanceledException, which escaped as a 500 (and from the warm-up
        // stopped the host) when cancellations were let through by type.
        var logged = new List<Exception>();
        var failure = await Assert.ThrowsAsync<QueryFailedException>(() =>
            AzureLogs.Guard<int>(() => throw new TaskCanceledException("network timeout"), logged.Add, CancellationToken.None));
        Assert.Contains("TaskCanceledException", failure.Message, StringComparison.Ordinal);
        Assert.Single(logged);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_a_query_failure()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AzureLogs.Guard<int>(() => throw new OperationCanceledException(cancel.Token), _ => { }, cancel.Token));
    }

    [Fact]
    public async Task A_query_that_answers_passes_through()
    {
        Assert.Equal(3, await AzureLogs.Guard(() => Task.FromResult(3), _ => { }, CancellationToken.None));
    }

    [Fact]
    public void Numbers_read_whatever_type_the_sdk_chose()
    {
        Assert.Equal(3.0, RowValues.Number(3L));
        Assert.Equal(3.0, RowValues.Number(3));
        Assert.Equal(1.5, RowValues.Number(1.5m));
        Assert.Null(RowValues.Number("3"));
        Assert.Equal(0, RowValues.Whole(null));
    }
}
