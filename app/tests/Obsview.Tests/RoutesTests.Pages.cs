using System.Net;
using System.Text.Json;
using Obsview.Web;

namespace Obsview.Tests;

public partial class RoutesTests
{
    private static List<Row> TwoGroups()
    {
        var now = DateTimeOffset.UtcNow;
        return [.. new[] { ("rg-platform-dev", "Log Analytics", 3.0), ("rg-gymlog-dev", "Gym compute", 4.0) }
            .Select(c => R.Of(("UsageDate", now), ("ResourceGroup", c.Item1), ("ServiceName", c.Item2), ("Cost", c.Item3), ("Currency", "USD")))];
    }

    private static FakeSource Only(string table, List<Row> rows) => new()
    {
        Rows = (kql, _) => kql.Split('\n')[0].Contains(table, StringComparison.Ordinal) ? rows : [],
    };

    [Fact]
    public async Task Security_headers_are_present()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await Get(client, "/login");
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Static_assets_are_served_with_the_headers()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        foreach (var path in new[] { "/static/app.js", "/static/app.css", "/static/icon.svg" })
        {
            using var response = await Get(client, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("Content-Security-Policy"), path);
        }
    }

    [Fact]
    public async Task No_passcode_means_no_session_even_with_a_forged_cookie()
    {
        // With no passcode the key would be sha256(""), which anyone can compute.
        using var app = new TestApp { Settings = new Settings { Passcode = "" } };
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var key = System.Security.Cryptography.SHA256.HashData([]);
        var forged = $"{issued}.{Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(key, System.Text.Encoding.ASCII.GetBytes(issued)))}";
        using var client = app.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/apps");
        request.Headers.Add("Cookie", $"{Session.CookieName}={forged}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("².abc")]
    [InlineData("12.abc")]
    [InlineData("nodot")]
    [InlineData("")]
    [InlineData(null)]
    public void A_malformed_session_cookie_is_invalid_not_an_error(string? token)
    {
        // "²" passes Python's isdigit(), and used to raise from encode("ascii"): a 500 on every page.
        var session = new Session(new Settings { Passcode = TestApp.Passcode }, TimeProvider.System);
        Assert.False(session.Valid(token));
    }

    [Fact]
    public void A_session_expires_after_its_max_age()
    {
        var clock = Clocks.At("2026-11-17T12:00:00Z");
        var session = new Session(new Settings { Passcode = TestApp.Passcode, CookieMaxAgeSeconds = 60 }, clock);
        var token = session.Issue();
        Assert.True(session.Valid(token));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(session.Valid(token));
    }

    [Fact]
    public void Rotating_the_passcode_signs_everyone_out_but_a_cookie_secret_survives_it()
    {
        var token = new Session(new Settings { Passcode = "old" }, TimeProvider.System).Issue();
        Assert.False(new Session(new Settings { Passcode = "new" }, TimeProvider.System).Valid(token));
        var pinned = new Session(new Settings { Passcode = "old", CookieSecret = "s3cret" }, TimeProvider.System).Issue();
        Assert.True(new Session(new Settings { Passcode = "new", CookieSecret = "s3cret" }, TimeProvider.System).Valid(pinned));
    }

    [Fact]
    public void A_non_ascii_passcode_is_compared_as_bytes() =>
        Assert.True(new Session(new Settings { Passcode = "£1" }, TimeProvider.System).CheckPasscode("£1"));

    [Fact]
    public async Task With_the_gate_off_pages_open_without_a_session()
    {
        using var app = new TestApp { Settings = new Settings { RequirePasscode = false }, Source = new FakeSource() };
        using var client = app.Browser();
        using var response = await Get(client, "/apps");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("30d")]
    [InlineData("")]
    [InlineData("-7")]
    public async Task A_malformed_range_falls_back_rather_than_erroring(string bad)
    {
        var recording = new FakeSource();
        using var app = new TestApp { Source = recording };
        using var client = await app.SignedIn();
        using var response = await Get(client, $"/apps?days={bad}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Deployments.Window(Queries.DefaultRange), recording.Calls.Select(c => c.Window));
    }

    [Fact]
    public async Task Platform_renders_demo_data()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/platform?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(["costs", "ops"], Payload(html).GetProperty("groups").EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("aria-current=\"page\"", html, StringComparison.Ordinal);
        foreach (var section in new[] { "id=\"ops\"", "id=\"costs\"", "id=\"errors\"" })
        {
            Assert.Contains(section, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Platform_shows_only_what_no_app_owns()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        var html = await Text(client, "/platform");
        Assert.Contains("azure-landingzone/hub", html, StringComparison.Ordinal);
        Assert.Contains("Session resources", html, StringComparison.Ordinal);
        Assert.Contains("rg-platform-dev", html, StringComparison.Ordinal);
        Assert.DoesNotContain("health/terraform", html, StringComparison.Ordinal);
        Assert.DoesNotContain("caj-gymlog-dev-backup", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rg-finances-dev", html, StringComparison.Ordinal);
        // The budget is the subscription's: its card is on Overall only.
        Assert.DoesNotContain("Forecast to month end", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Forecast to month end", await Text(client, "/apps/health"), StringComparison.Ordinal);
        Assert.Contains("Forecast to month end", await Text(client, "/overall"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/platform?days=1", new[] { "Log Analytics", "$3.00" }, new[] { "Gym compute", "$7.00", "$4.00" })]
    [InlineData("/apps/health?days=1", new[] { "Gym compute", "$4.00" }, new[] { "Log Analytics", "$7.00", "$3.00" })]
    public async Task A_slices_costs_show_only_its_own_spend(string path, string[] own, string[] other)
    {
        using var app = new TestApp { Source = Only("Costs_CL", TwoGroups()) };
        using var client = await app.SignedIn();
        var html = await Text(client, path);
        Assert.All(own, s => Assert.Contains(s, html, StringComparison.Ordinal));
        Assert.All(other, s => Assert.DoesNotContain(s, html, StringComparison.Ordinal));
        var keys = Payload(html).GetProperty("groups").GetProperty("costs").GetProperty("series_keys").EnumerateArray().Select(k => k.GetString()).ToList();
        // Stacked by the slice's own services, never the estate's areas.
        Assert.Equal([own[0]], keys);
    }

    [Fact]
    public async Task Overall_breaks_down_every_resource_group()
    {
        using var app = new TestApp { Source = Only("Costs_CL", TwoGroups()) };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/overall?days=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div class=\"hero-number\">$7.00</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"value\">$7.00</div>", html, StringComparison.Ordinal);
        foreach (var s in new[] { "By area", "rg-platform-dev", "rg-gymlog-dev", "Log Analytics", "Gym compute" })
        {
            Assert.Contains(s, html, StringComparison.Ordinal);
        }

        var charts = Payload(html).GetProperty("groups").GetProperty("costs");
        Assert.Equal(["platform", "apps"], charts.GetProperty("series_keys").EnumerateArray().Take(2).Select(k => k.GetString()));
        Assert.Equal(["platform", "apps"], charts.GetProperty("treemap").EnumerateArray().Select(n => n.GetProperty("name").GetString()));
        Assert.True(charts.GetProperty("by_area").GetBoolean());
    }

    [Fact]
    public async Task Overall_renders_demo_data()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/overall?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-group=\"costs\" data-chart=\"treemap\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overall_query_failure_is_a_page()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/overall");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("/overall")]
    [InlineData("/apps")]
    [InlineData("/apps/health")]
    [InlineData("/platform")]
    [InlineData("/slo")]
    [InlineData("/slo/health")]
    public async Task Every_page_needs_a_session(string path)
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await Get(client, path);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task Platform_renders_with_no_rows()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/platform");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Platform_says_when_an_always_reporting_check_goes_quiet()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        var html = await Text(client, "/platform");
        Assert.Contains("2 problems now", html, StringComparison.Ordinal);
        Assert.Contains("Session resources <small>no row</small>", html, StringComparison.Ordinal);
        Assert.Contains("Cost feed <small>no row</small>", html, StringComparison.Ordinal);
        // An app page has no such checks, so it says nothing about them.
        Assert.DoesNotContain("no row", await Text(client, "/apps/health"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Platform_with_both_checks_reporting_is_all_clear()
    {
        var now = DateTimeOffset.UtcNow;
        List<Row> rows = [.. new[] { ("session", "vm"), ("cost-feed", "Costs_CL") }
            .Select(e => R.Of(("TimeGenerated", now), ("Kind", e.Item1), ("Name", e.Item2), ("Status", "ok"), ("Detail", "")))];
        using var app = new TestApp { Source = Only("OpsEvents_CL", rows) };
        using var client = await app.SignedIn();
        var html = await Text(client, "/platform");
        Assert.Contains("All clear", html, StringComparison.Ordinal);
        Assert.DoesNotContain("no row", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warm_up_fills_the_default_pages_queries()
    {
        var spy = new FakeSource();
        var settings = new Settings { Passcode = TestApp.Passcode };
        var queries = new Queries(spy, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<Queries>.Instance);
        await queries.WarmAsync();
        Assert.Contains(queries.ReadyQuery(), spy.Calls);
        Assert.Equal(queries.HomeQueries(Queries.DefaultRange, DateTimeOffset.UtcNow).Count, spy.Calls.Count);
    }

    [Fact]
    public async Task The_warm_up_runs_at_start_when_a_workspace_is_configured()
    {
        var spy = new FakeSource();
        using var app = new TestApp { Source = spy, Warm = true, Settings = new Settings { Passcode = TestApp.Passcode, WorkspaceId = "w" } };
        using var client = app.Browser();
        var expected = new Queries(spy, new Settings(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Queries>.Instance).HomeQueries(Queries.DefaultRange, DateTimeOffset.UtcNow).Count;
        for (var i = 0; i < 100 && spy.Calls.Count < expected; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(expected, spy.Calls.Count);
    }

    [Fact]
    public async Task Platform_renders_around_every_query_failing()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/platform");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Couldn't read the checks", html, StringComparison.Ordinal);
        Assert.Contains("Couldn't read the costs", html, StringComparison.Ordinal);
        Assert.Contains("Couldn't read the console logs", html, StringComparison.Ordinal);
        Assert.DoesNotContain("echarts", html, StringComparison.Ordinal);
    }

    /// <summary>OpsEvents_CL answers; ContainerAppSystemLogs_CL does not.</summary>
    private static FakeSource FailedRunsBroken()
    {
        var demo = new DemoLogs(DateTimeOffset.UtcNow);
        var source = new FakeSource { Rows = (kql, span) => demo.QueryAsync(kql, span).Result };
        source.Failing.Add("ContainerAppSystemLogs_CL");
        return source;
    }

    [Fact]
    public async Task Platform_survives_the_failed_runs_query_failing()
    {
        using var app = new TestApp { Source = FailedRunsBroken() };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/platform");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("read the failed runs", html, StringComparison.Ordinal);
        Assert.Contains("Jobs and checks", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readyz_fails_without_a_workspace_unless_demo_is_explicit()
    {
        // No WORKSPACE_ID: demo data by default, which in Azure means a
        // missing setting, so the smoke test must fail it.
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await Get(client, "/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no WORKSPACE_ID", JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Readyz_reports_a_failing_workspace()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true }, Settings = new Settings { Passcode = TestApp.Passcode, WorkspaceId = "00000000-0000-0000-0000-000000000000" } };
        using var client = app.Browser();
        using var response = await Get(client, "/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("boom", JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readyz_passes_when_the_workspace_answers()
    {
        var recording = new FakeSource();
        var settings = new Settings { Passcode = TestApp.Passcode, WorkspaceId = "00000000-0000-0000-0000-000000000000" };
        using var app = new TestApp { Source = recording, Settings = settings };
        using var client = app.Browser();
        using var response = await Get(client, "/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Deployments.Window(Queries.DefaultRange)], recording.Calls.Select(c => c.Window));
    }

    [Fact]
    public async Task Readyz_passes_on_explicit_demo()
    {
        // DEMO=true with no workspace is a deliberate demo, not a misconfiguration.
        using var app = new TestApp { Settings = new Settings { Passcode = TestApp.Passcode, Demo = true } };
        using var client = app.Browser();
        using var response = await Get(client, "/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_manifest_names_the_app()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        var manifest = JsonElement.Parse(await Text(client, "/manifest.json"));
        Assert.Equal("Observability", manifest.GetProperty("name").GetString());
        Assert.Equal("/static/icon.svg", manifest.GetProperty("icons")[0].GetProperty("src").GetString());
    }
}
