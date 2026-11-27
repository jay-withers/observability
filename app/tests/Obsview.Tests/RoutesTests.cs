using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Obsview.Web;

namespace Obsview.Tests;

/// <summary>The pages through the app, as a browser sees them: sign-in, rendering, failures and redirects.</summary>
[Collection(nameof(ImageTag))]
public partial class RoutesTests
{
    [GeneratedRegex("""<script type="application/json" id="chart-data">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex ChartData();

    [GeneratedRegex("""<a class="card nav-tile".*?</a>""", RegexOptions.Singleline)]
    private static partial Regex NavTile();

    [GeneratedRegex("<a class=\"card nav-tile\" href=\"([^\"]+)\"")]
    private static partial Regex NavTileHref();

    internal static async Task<string> Text(HttpClient client, string path) => await client.GetStringAsync(new Uri(path, UriKind.Relative));

    internal static async Task<HttpResponseMessage> Get(HttpClient client, string path) => await client.GetAsync(new Uri(path, UriKind.Relative));

    internal static JsonElement Payload(string html)
    {
        var match = ChartData().Match(html);
        Assert.True(match.Success, "no chart payload on the page");
        return JsonElement.Parse(match.Groups[1].Value);
    }

    internal static List<string> Tiles(string html) => [.. NavTile().Matches(html).Select(m => m.Value)];

    [Fact]
    public async Task Healthz_is_open_and_reports_the_tag()
    {
        using var tag = ImageTag.Set("v9.9.9");
        using var app = new TestApp();
        using var client = app.Browser();
        var body = JsonElement.Parse(await Text(client, "/healthz"));
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("v9.9.9", body.GetProperty("version").GetString());
    }

    [Fact]
    public async Task The_hosts_own_environment_name_never_becomes_the_kql_filter()
    {
        // Regression: settings were read from the host's configuration, where
        // the ASPNETCORE_/DOTNET_ "ENVIRONMENT" (this host's is "Testing")
        // stood in for an unset ENVIRONMENT and filtered every query on it.
        using var app = new TestApp { RealSettings = true };
        using var client = app.Browser();
        var body = JsonElement.Parse(await Text(client, "/healthz"));
        Assert.Equal(System.Environment.GetEnvironmentVariable("ENVIRONMENT") ?? "dev", body.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task Pages_redirect_to_login_without_a_session()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await Get(client, "/apps");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Wrong_passcode_is_refused()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await TestApp.LogIn(client, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(Session.CookieName, StringComparison.Ordinal)));
        Assert.Contains("That is not it.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_login_form_carries_a_csrf_token()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var page = await Get(client, "/login");
        var cookie = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(Csrf.CookieName + "=", StringComparison.Ordinal));
        var value = cookie[(Csrf.CookieName.Length + 1)..].Split(';')[0];
        Assert.Equal(value, Csrf.TokenIn(await page.Content.ReadAsStringAsync()));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_login_without_the_csrf_token_is_refused_even_with_the_passcode()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        await Text(client, "/login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["passcode"] = TestApp.Passcode });
        using var response = await client.PostAsync(new Uri("/login", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("expired", html, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(Session.CookieName, StringComparison.Ordinal)));
        // The re-render carries a token that works, so a retry signs in.
        using var retry = new FormUrlEncodedContent(new Dictionary<string, string> { ["passcode"] = TestApp.Passcode, ["csrf_token"] = Csrf.TokenIn(html) });
        using var again = await client.PostAsync(new Uri("/login", UriKind.Relative), retry);
        Assert.Equal(HttpStatusCode.SeeOther, again.StatusCode);
    }

    [Fact]
    public async Task A_browser_that_keeps_no_cookie_is_told_cookies_are_needed()
    {
        using var app = new TestApp();
        using var keeper = app.Browser();
        var token = Csrf.TokenIn(await Text(keeper, "/login"));
        using var forgetful = app.Browser();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["passcode"] = TestApp.Passcode, ["csrf_token"] = token });
        using var response = await forgetful.PostAsync(new Uri("/login", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("needs cookies", html, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cross_site_login_is_refused()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await TestApp.LogIn(client, TestApp.Passcode, new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task Apps_index_renders_all_apps_deploys_with_chart_payload()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Demo data", html, StringComparison.Ordinal);
        var payload = Payload(html);
        Assert.Equal(30, payload.GetProperty("days").GetInt32());
        var charts = payload.GetProperty("groups").GetProperty("deploy");
        foreach (var key in new[] { "dates", "outcomes", "timeline", "calendar", "lead" })
        {
            Assert.True(charts.TryGetProperty(key, out _), key);
        }

        Assert.Equal(30, charts.GetProperty("dates").GetArrayLength());
        Assert.Contains("data-group=\"deploy\" data-chart=\"outcomes\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_is_a_tile_per_page()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(["/apps?days=30", "/slo?days=30", "/platform?days=30", "/overall?days=30"], NavTileHref().Matches(html).Select(m => m.Groups[1].Value));
        Assert.Contains("spent in", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_needs_a_session()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await Get(client, "/");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_brand_opens_home_at_the_same_range()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        Assert.Contains("<a class=\"brand\" href=\"/?days=30\">", await Text(client, "/apps?days=30"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_splits_problems_and_errors_between_apps_and_the_platform()
    {
        var now = DateTimeOffset.UtcNow;
        var ops = new (string Kind, string Name, string Status)[]
        {
            ("heartbeat", "caj-gymlog-dev-backup", "overdue"), // health's
            ("drift", "finances/terraform", "drifted"), // finances'
            ("drift", "azure-landingzone/hub", "drifted"), // the platform's
            ("session", "vm", "ok"),
            ("cost-feed", "Costs_CL", "ok"),
        }.Select(e => R.Of(("TimeGenerated", now), ("Kind", e.Kind), ("Name", e.Name), ("Status", e.Status), ("Detail", ""))).ToList();
        // The last is a repo no longer listed: on no app's tile, so not in the sum.
        var deploys = new[] { "health", "finances", "retired-app" }.Select(r => R.Of(("TimeGenerated", now), ("Repo", r), ("Outcome", "success"))).ToList();
        List<Row> errors =
        [
            R.Of(("Workload", "ca-gymlog-dev"), ("Total", 4L), ("TimeGenerated", now), ("Log_s", "ERROR x")),
            R.Of(("Workload", "ca-platform-thing"), ("Total", 3L), ("TimeGenerated", now), ("Log_s", "ERROR y")),
        ];
        var source = new FakeSource
        {
            Rows = (kql, _) =>
            {
                var first = kql.Split('\n')[0];
                return first.Contains("OpsEvents_CL", StringComparison.Ordinal) ? ops
                    : first.Contains("ContainerAppConsoleLogs", StringComparison.Ordinal) ? errors
                    : first.Contains("Deployments_CL", StringComparison.Ordinal) ? deploys
                    : [];
            },
        };
        using var app = new TestApp { Source = source };
        using var client = await app.SignedIn();
        var tiles = Tiles(await Text(client, "/"));
        var (appsTile, platformTile) = (tiles[0], tiles[2]);
        Assert.Contains("2 problems", appsTile, StringComparison.Ordinal);
        Assert.Contains("<div class=\"value\">2</div>", appsTile, StringComparison.Ordinal);
        Assert.Contains("4 error lines", appsTile, StringComparison.Ordinal);
        Assert.Contains("<div class=\"value\">1</div>", platformTile, StringComparison.Ordinal);
        // The big number is the count; the badge doesn't say it again.
        Assert.Contains("needs attention", platformTile, StringComparison.Ordinal);
        Assert.DoesNotContain("problem</span>", platformTile, StringComparison.Ordinal);
        Assert.Contains("3 error lines", platformTile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_counts_a_quiet_always_reporting_check_as_a_platform_problem()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        // Session resources and the cost feed, as on /platform.
        Assert.Contains("<div class=\"value\">2</div>", Tiles(await Text(client, "/"))[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_with_no_budget_says_so_rather_than_over_budget()
    {
        using var app = new TestApp { Settings = new Settings { Passcode = TestApp.Passcode, Budget = 0 } };
        using var client = await app.SignedIn();
        var overall = Tiles(await Text(client, "/"))[3];
        Assert.DoesNotContain("budget</span>", overall, StringComparison.Ordinal);
        Assert.Contains("No budget set", overall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_renders_around_every_query_failing()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        foreach (var what in new[] { "Deploys", "Checks", "Costs", "SLOs" })
        {
            Assert.Contains($"{what} unavailable", html, StringComparison.Ordinal);
        }

        // Regression: the SLO tile said "partly unavailable" over "SLOs unavailable".
        Assert.DoesNotContain("partly unavailable", html, StringComparison.Ordinal);
        Assert.Equal(4, Tiles(html).Count);
    }

    [Fact]
    public async Task Home_reads_the_warmed_queries()
    {
        var spy = new FakeSource();
        using var app = new TestApp { Source = spy };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var queries = (Queries)app.Services.GetService(typeof(Queries))!;
        var warmed = queries.HomeQueries(Queries.DefaultRange, DateTimeOffset.UtcNow);
        Assert.Equal(warmed.Values.Select(v => v.Kql).Order(), spy.Calls.Select(c => c.Kql).Order());
    }

    [Theory]
    [InlineData("/deployments?days=7", "/apps?days=7")]
    [InlineData("/ops?days=90", "/platform?days=90")]
    [InlineData("/costs", "/overall?days=7")]
    [InlineData("/costs?days=junk", "/overall?days=7")]
    // Regression: these routed to the action and then missed its lookup, a 500.
    [InlineData("/ops/", "/platform?days=7")]
    [InlineData("/Costs?days=30", "/overall?days=30")]
    public async Task The_old_views_redirect(string old, string target)
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, old);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(target, response.Headers.Location!.OriginalString);
    }

    [GeneratedRegex("""<nav class="tabs".*?</nav>""", RegexOptions.Singleline)]
    private static partial Regex Nav();

    [GeneratedRegex("""href="(/[a-z]+)\?""")]
    private static partial Regex NavHref();

    [Fact]
    public async Task The_nav_is_apps_slos_platform_and_overall()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        var nav = Nav().Match(await Text(client, "/apps"));
        Assert.True(nav.Success);
        Assert.Equal(["/apps", "/slo", "/platform", "/overall"], NavHref().Matches(nav.Value).Select(m => m.Groups[1].Value));
    }

    [Fact]
    public async Task Unknown_range_falls_back_to_the_default()
    {
        var recording = new FakeSource();
        using var app = new TestApp { Source = recording };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps?days=12345");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Deployments.Window(Queries.DefaultRange), recording.Calls.Select(c => c.Window));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No deploys in the last 7 days", html, StringComparison.Ordinal);
        // No charts to draw, so no chart script either.
        Assert.DoesNotContain("echarts", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_failure_is_a_page_not_a_500()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Couldn't load this view", html, StringComparison.Ordinal);
        Assert.Contains("boom", html, StringComparison.Ordinal);
    }
}

/// <summary>
/// <c>IMAGE_TAG</c> is read from the process environment, so the tests that
/// set it run one at a time, and put it back.
/// </summary>
[CollectionDefinition(nameof(ImageTag), DisableParallelization = true)]
public sealed class ImageTag : IDisposable
{
    private readonly string? _previous = System.Environment.GetEnvironmentVariable("IMAGE_TAG");

    private ImageTag(string? value) => System.Environment.SetEnvironmentVariable("IMAGE_TAG", value);

    public static ImageTag Set(string? value) => new(value);

    public void Dispose() => System.Environment.SetEnvironmentVariable("IMAGE_TAG", _previous);
}
