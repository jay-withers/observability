using System.Net;
using System.Text.RegularExpressions;

namespace Obsview.Tests;

public partial class RoutesTests
{
    [GeneratedRegex("""<nav class="jump".*?</nav>""", RegexOptions.Singleline)]
    private static partial Regex Jump();

    [GeneratedRegex("href=\"#([a-z]+)\"")]
    private static partial Regex Anchor();

    [GeneratedRegex("""href="/slo/repo-agent.*?</a>""", RegexOptions.Singleline)]
    private static partial Regex RepoAgentSloTile();

    [GeneratedRegex("""<a class="card nav-tile" href="/slo\?days=7".*?</a>""", RegexOptions.Singleline)]
    private static partial Regex HomeSloTile();

    [GeneratedRegex("""<div class="value">1<small class="of"> of 2</small>""")]
    private static partial Regex OneOfTwo();

    /// <summary>Deploys come from the demo; every other query fails.</summary>
    private static FakeSource OnlyDeploysAnswer()
    {
        var demo = new DemoLogs(DateTimeOffset.UtcNow);
        var source = new FakeSource { Rows = (kql, span) => demo.QueryAsync(kql, span).Result };
        foreach (var table in new[] { "OpsEvents_CL", "Costs_CL", "ContainerApp" })
        {
            source.Failing.Add(table);
        }

        return source;
    }

    [Fact]
    public async Task Apps_index_lists_every_app_with_demo_data()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.All(Apps.All, a => Assert.Contains($"href=\"/apps/{a.Slug}?days=7\"", html, StringComparison.Ordinal));
        Assert.Contains("error line", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("finances")]
    [InlineData("market-agent")]
    [InlineData("career")]
    public async Task App_page_renders_every_section_with_demo_data(string slug)
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, $"/apps/{slug}?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        foreach (var section in new[] { "id=\"errors\"", "id=\"deployments\"", "id=\"ops\"", "id=\"costs\"" })
        {
            Assert.Contains(section, html, StringComparison.Ordinal);
        }

        Assert.Equal(["costs", "deploy", "ops"], Payload(html).GetProperty("groups").EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("data-group=\"deploy\" data-chart=\"outcomes\"", html, StringComparison.Ordinal);
        // Estate-only cards stay on the estate views.
        Assert.DoesNotContain("Sessions running", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Forecast to month end", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_page_shows_only_that_apps_rows()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        var html = await Text(client, "/apps/health");
        Assert.Contains("caj-gymlog-dev-backup", html, StringComparison.Ordinal);
        Assert.DoesNotContain("caj-finances-dev-backup", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rg-platform-dev", html, StringComparison.Ordinal);
        Assert.Contains("GET /sessions/N → 500", html, StringComparison.Ordinal);
    }

    /// <summary>A bookmark from before health was renamed from gym-log.</summary>
    [Theory]
    [InlineData("apps")]
    [InlineData("slo")]
    public async Task An_apps_former_name_redirects_to_its_page(string page)
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        using var response = await Get(client, $"/{page}/gym-log?days=30");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"/{page}/health?days=30", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/apps/nope")]
    [InlineData("/slo/nope")]
    public async Task Unknown_app_is_404(string path)
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        using var response = await Get(client, path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task App_pages_render_with_no_rows()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        using (var index = await Get(client, "/apps"))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        }

        using var response = await Get(client, "/apps/career?days=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No errors logged in the last 1 day.", html, StringComparison.Ordinal);
        Assert.Contains("No checks or failed runs for Career in this range.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_pages_render_around_failed_secondary_queries()
    {
        using var app = new TestApp { Source = OnlyDeploysAnswer() };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Couldn't read the console logs", html, StringComparison.Ordinal);
        Assert.Contains("Couldn't read the checks", html, StringComparison.Ordinal);
        Assert.Contains("Couldn't read the costs", html, StringComparison.Ordinal);
        using var index = await Get(client, "/apps");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        var indexHtml = await index.Content.ReadAsStringAsync();
        Assert.Contains("spend unavailable", indexHtml, StringComparison.Ordinal);
        Assert.Contains("errors unavailable", indexHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/apps/health")]
    [InlineData("/apps")]
    public async Task App_page_deploy_failure_is_a_page(string path)
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using var response = await Get(client, path);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public async Task Every_range_is_offered_and_honoured(int days)
    {
        var recording = new FakeSource();
        using var app = new TestApp { Source = recording };
        using var client = await app.SignedIn();
        var html = await Text(client, $"/apps?days={days}");
        Assert.Contains(Deployments.Window(days), recording.Calls.Select(c => c.Window));
        Assert.All(Web.Queries.Ranges, r => Assert.Contains($"href=\"?days={r}\"", html, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deployment_app_cards_link_to_the_app_page()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        var html = await Text(client, "/apps");
        var deploys = html[html.IndexOf("id=\"deployments\"", StringComparison.Ordinal)..];
        Assert.Contains("href=\"/apps/health?days=7\"", deploys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_page_with_no_checks_does_not_hide_a_failed_runs_failure()
    {
        var source = new FakeSource();
        source.Failing.Add("ContainerAppSystemLogs_CL");
        using var app = new TestApp { Source = source };
        using var client = await app.SignedIn();
        var html = await Text(client, "/apps/career");
        Assert.DoesNotContain("No checks or failed runs", html, StringComparison.Ordinal);
        Assert.Contains("read the failed runs", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apps_index_does_not_run_the_failed_runs_query()
    {
        var spy = new FakeSource();
        using var app = new TestApp { Source = spy };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/apps");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(spy.Calls, c => c.Kql.Contains("ContainerAppSystemLogs_CL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_default_range_is_seven_days()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        Assert.Contains("<a href=\"?days=7\" aria-current=\"true\">7d</a>", await Text(client, "/apps"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/apps/health")]
    [InlineData("/platform")]
    public async Task Section_links_are_on_the_page_they_jump_within(string path)
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        var html = await Text(client, path);
        var nav = Jump().Match(html);
        Assert.True(nav.Success);
        Assert.All(Anchor().Matches(nav.Value), m => Assert.Contains($"id=\"{m.Groups[1].Value}\"", html, StringComparison.Ordinal));
    }

    // --- SLOs ----------------------------------------------------------------

    [Fact]
    public async Task Slo_index_is_a_tile_per_app_linking_to_its_page()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, "/slo?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(Apps.All.Select(a => $"/slo/{a.Slug}?days=30"), NavTileHref().Matches(html).Select(m => m.Groups[1].Value));
        Assert.Contains("budget left", html, StringComparison.Ordinal);
        // The window is fixed, so no range to pick.
        Assert.DoesNotContain("class=\"range\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slo_index_leaves_request_slis_off_job_only_apps()
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        var tile = RepoAgentSloTile().Match(await Text(client, "/slo"));
        Assert.True(tile.Success);
        Assert.Contains("Job runs", tile.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Availability", tile.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("repo-agent")]
    [InlineData("observability")]
    public async Task Slo_app_page_renders_each_sli_with_a_burndown(string slug)
    {
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var response = await Get(client, $"/slo/{slug}?days=7");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"href=\"/apps/{slug}?days=7\"", html, StringComparison.Ordinal);
        var burndown = Payload(html).GetProperty("groups").GetProperty("slo").GetProperty("burndown");
        Assert.NotEmpty(burndown.EnumerateObject());
        foreach (var chart in burndown.EnumerateObject())
        {
            Assert.Contains($"data-key=\"{chart.Name}\"", html, StringComparison.Ordinal);
            Assert.Equal(Slo.WindowDays, chart.Value.GetProperty("points").GetArrayLength());
        }
    }

    [Fact]
    public async Task Slo_app_page_says_when_an_sli_has_no_events()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        using var response = await Get(client, "/slo/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("No requests in the last 28 days", html, StringComparison.Ordinal);
        Assert.Contains("No runs in the last 28 days", html, StringComparison.Ordinal);
        // Nothing to draw, so no charts loaded.
        Assert.DoesNotContain("echarts", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slo_pages_render_around_every_query_failing()
    {
        using var app = new TestApp { Source = new FakeSource { FailAll = true } };
        using var client = await app.SignedIn();
        using (var index = await Get(client, "/slo"))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains("SLIs unavailable", await index.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var page = await Get(client, "/slo/finances");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(4, Regex.Count(await page.Content.ReadAsStringAsync(), "Couldn't read this SLI"));
    }

    [Fact]
    public async Task Slo_pages_share_the_deploy_query_with_the_other_pages()
    {
        var spy = new FakeSource();
        using var app = new TestApp { Source = spy };
        using var client = await app.SignedIn();
        await Text(client, "/slo?days=30");
        Assert.Contains((Deployments.Kql("dev"), Deployments.Window(30)), spy.Calls);
        Assert.Contains((Slo.HttpKql, Slo.Window), spy.Calls);
        Assert.Contains((Slo.JobsKql, Slo.Window), spy.Calls);
    }

    [Fact]
    public async Task App_page_links_to_its_slo_page()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        Assert.Contains("href=\"/slo/career?days=7\"", await Text(client, "/apps/career"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_slo_tile_counts_apps_within_budget()
    {
        var now = DateTimeOffset.UtcNow;
        // health: every deploy fails (budget spent); finances: all good.
        List<Row> rows = [.. from h in Enumerable.Range(1, 3)
                             from d in new[] { ("health", "failed"), ("finances", "success") }
                             select R.Of(("TimeGenerated", now.AddHours(-h)), ("Repo", d.Item1), ("App", d.Item1), ("Outcome", d.Item2), ("Trigger", "merge"))];
        using var app = new TestApp { Source = new FakeSource { Rows = (kql, _) => kql.StartsWith("Deployments_CL", StringComparison.Ordinal) ? rows : [] } };
        using var client = await app.SignedIn();
        var tile = HomeSloTile().Match(await Text(client, "/"));
        Assert.True(tile.Success);
        Assert.Contains("1 spent", tile.Value, StringComparison.Ordinal);
        Assert.Matches(OneOfTwo(), tile.Value);
    }

    [Fact]
    public async Task Home_slo_tile_with_no_events_says_so_not_0_of_0()
    {
        using var app = new TestApp { Source = new FakeSource() };
        using var client = await app.SignedIn();
        var tile = HomeSloTile().Match(await Text(client, "/"));
        Assert.True(tile.Success);
        Assert.Contains("No SLO events in 28 days", tile.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(" of 0", tile.Value, StringComparison.Ordinal);
    }

    // --- the release in the header ---------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/apps")]
    [InlineData("/platform")]
    [InlineData("/overall")]
    [InlineData("/slo")]
    public async Task A_signed_in_page_shows_the_release_beside_the_name(string path)
    {
        using var tag = ImageTag.Set("v1.2.3");
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var page = await Get(client, path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<span class=\"version\">v1.2.3</span>", html, StringComparison.Ordinal);
        // Beside the home link, not part of its name.
        var brand = html.Split("class=\"brand\"", 2)[1].Split("</a>", 2)[0];
        Assert.DoesNotContain("v1.2.3", brand, StringComparison.Ordinal);
    }

    /// <summary>Not before the passcode. Not a secret either: /healthz reports it.</summary>
    [Fact]
    public async Task The_sign_in_page_never_shows_the_release()
    {
        using var tag = ImageTag.Set("v1.2.3");
        using var app = new TestApp();
        using var client = app.Browser();
        Assert.DoesNotContain("v1.2.3", await Text(client, "/login"), StringComparison.Ordinal);
    }

    /// <summary>A local run has no IMAGE_TAG; it shows nothing rather than a blank tag.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task No_release_is_shown_when_none_is_deployed(string? value)
    {
        using var tag = ImageTag.Set(value);
        using var app = new TestApp();
        using var client = await app.SignedIn();
        using var page = await Get(client, "/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("class=\"version\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
