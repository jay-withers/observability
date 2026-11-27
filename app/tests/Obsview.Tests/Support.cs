using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Obsview.Web;

namespace Obsview.Tests;

/// <summary>Rows as the SDK gives them, written compactly.</summary>
internal static class R
{
    public static Row Of(params (string Key, object? Value)[] values)
    {
        var row = new Row();
        foreach (var (key, value) in values)
        {
            row[key] = value;
        }

        return row;
    }

    public static DateTimeOffset At(string iso) => Times.Parse(iso) ?? throw new ArgumentException(iso);

    public static DateOnly Day(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A source the tests control: fixed rows per table, a failing table, and a record of what ran.</summary>
internal sealed class FakeSource : ILogsSource
{
    private readonly object _gate = new();

    public Func<string, TimeSpan, IReadOnlyList<Row>> Rows { get; set; } = (_, _) => [];

    /// <summary>Queries whose KQL contains any of these fail.</summary>
    public HashSet<string> Failing { get; } = [];

    public bool FailAll { get; set; }

    public List<(string Kql, TimeSpan Window)> Calls { get; } = [];

    public Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Calls.Add((kql, timespan));
        }

        if (FailAll || Failing.Any(kql.Contains))
        {
            throw new QueryFailedException("Log Analytics query failed: boom");
        }

        return Task.FromResult(Rows(kql, timespan));
    }
}

/// <summary>
/// The app, hosted in memory: demo data unless a source is given, a known
/// passcode, and a fixed clock when one is given. Clients speak https, as the
/// cookies are Secure.
/// </summary>
internal sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Passcode = "sesame";

    public Settings Settings { get; init; } = new() { Passcode = Passcode };

    /// <summary>Keep the app's own settings, read from the environment as in production, rather than <see cref="Settings"/>.</summary>
    public bool RealSettings { get; init; }

    public ILogsSource? Source { get; init; }

    public TimeProvider? Clock { get; init; }

    /// <summary>Run the start-up cache warm-up, which otherwise adds its queries to a test's record.</summary>
    public bool Warm { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
        builder.ConfigureTestServices(services =>
        {
            if (!RealSettings)
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(Settings);
            }
            if (Source is not null)
            {
                services.RemoveAll<ILogsSource>();
                services.AddSingleton(Source);
            }

            if (!Warm)
            {
                foreach (var warmer in services.Where(d => d.ImplementationType == typeof(Warmer)).ToList())
                {
                    services.Remove(warmer);
                }
            }

            if (Clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(Clock);
            }
        });
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://testserver"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A client signed in through the form, as a browser would be.</summary>
    public async Task<HttpClient> SignedIn()
    {
        var client = Browser();
        using var response = await LogIn(client, Passcode);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        return client;
    }

    public static async Task<HttpResponseMessage> LogIn(HttpClient client, string passcode, IDictionary<string, string>? headers = null)
    {
        var token = Csrf.TokenIn(await client.GetStringAsync(new Uri("/login", UriKind.Relative)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["passcode"] = passcode, ["csrf_token"] = token }),
        };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }
}

internal static class Clocks
{
    public static FakeTimeProvider At(string iso) => new(R.At(iso));
}
