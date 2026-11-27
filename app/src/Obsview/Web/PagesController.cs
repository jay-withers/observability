using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Obsview.Web;

/// <summary>A 303 to the sign-in form, rather than a 401 a browser shows as an error.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSessionAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.HttpContext.RequestServices;
        if (!services.GetRequiredService<Settings>().RequirePasscode)
        {
            return;
        }

        if (services.GetRequiredService<Session>().Valid(context.HttpContext.Request.Cookies[Session.CookieName]))
        {
            return;
        }

        context.Result = new SeeOther("/login");
    }
}

/// <summary>A 303 See Other: never a 301, which a browser keeps for good.</summary>
public sealed class SeeOther(string location) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.HttpContext.Response.Headers.Location = location;
        return Task.CompletedTask;
    }
}

/// <summary>The sign-in form: the only page open without a session.</summary>
public sealed class LoginController(Settings settings, Session session) : Controller
{
    [HttpGet("/login")]
    public IActionResult Form() => View("Login", Page(""));

    [HttpPost("/login")]
    public IActionResult SignIn([FromForm] string? passcode, [FromForm(Name = "csrf_token")] string? csrfToken)
    {
        // Before the passcode, so a forged post can't even try one. The
        // re-render carries a fresh token, so a retry works.
        if (!Csrf.Valid(Request, csrfToken ?? ""))
        {
            // No cookie at all means the browser won't keep it (cookies
            // blocked, or plain http on a host other than localhost, where a
            // Secure cookie is dropped), and "try again" would fail the same way.
            var why = Request.Cookies.ContainsKey(Csrf.CookieName)
                ? "That form had expired. Try again."
                : "Signing in needs cookies, over https or on localhost.";
            return Refuse(why, StatusCodes.Status403Forbidden);
        }

        if (!session.CheckPasscode(passcode ?? ""))
        {
            return Refuse("That is not it.", StatusCodes.Status401Unauthorized);
        }

        Response.Cookies.Append(Session.CookieName, session.Issue(), new CookieOptions
        {
            MaxAge = TimeSpan.FromSeconds(settings.CookieMaxAgeSeconds),
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = true,
        });
        return new SeeOther("/");
    }

    private ViewResult Refuse(string error, int status)
    {
        var view = View("Login", Page(error));
        view.StatusCode = status;
        return view;
    }

    private LoginPage Page(string error) => new()
    {
        Page = "login",
        Error = error,
        CsrfToken = Csrf.Token(HttpContext),
        Environment = settings.Environment,
    };
}

/// <summary>
/// The pages. Each reads its queries at once (<see cref="Queries.GatherAsync"/>)
/// and builds its views from the rows; a failed secondary query leaves its
/// card saying so and the rest of the page renders.
/// </summary>
[RequireSession]
public sealed class PagesController(Queries queries, Settings settings, TimeProvider clock) : Controller
{
    /// <summary>
    /// Checks that write a row on every run, so no row in 48 hours means the
    /// check itself has stopped: said as a problem, not left as an absent
    /// section that reads like nothing running.
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysReport = ["session", "cost-feed"];


    private static string Release() => (System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "").Trim();

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Queries.Json);

    [HttpGet("/")]
    public async Task<IActionResult> Home(string? days, CancellationToken cancellationToken)
    {
        // A tile per page, each with its headline: the apps' deploys, problems
        // and errors, the platform's, and the month's spend against the budget.
        // Every query is optional here: a failed one leaves its figures
        // "unavailable", and each tile still opens its page.
        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var got = await queries.GatherAsync(queries.HomeQueries(span, now), cancellationToken).ConfigureAwait(false);
        var deployRows = queries.Ok(got["deploys"], "deploys");
        var opsRows = queries.Ok(got["ops"], "ops");
        var errorRows = queries.Ok(got["errors"], "errors");
        var costRows = queries.Ok(got["costs"], "costs");

        List<Deploy>? deploys = null;
        if (deployRows is not null)
        {
            var since = Deployments.RangeStart(span, now);
            var slugs = Apps.All.Select(a => a.Slug).ToHashSet(StringComparer.Ordinal);
            // The apps' tiles' sum: a deploy of a repo no longer listed is on no tile.
            deploys = [.. Apps.ParseDeploys(deployRows).Where(d => d.When >= since && slugs.Contains(d.Repo))];
        }

        int? appProblems = null;
        int? platformProblems = null;
        if (opsRows is not null)
        {
            var current = Ops.Latest(Ops.Parse(opsRows), now).Values.ToList();
            var platform = Apps.PlatformEvents(current);
            // The apps' tiles' sum, as Apps.Summarise counts each.
            appProblems = Apps.All.Sum(a => Apps.EventsFor(a, current).Count(e => e.Problem));
            platformProblems = platform.Count(e => e.Problem) + Silent(platform.Select(e => e.Kind)).Count;
        }

        int? appErrors = null;
        int? platformErrors = null;
        if (errorRows is not null)
        {
            var totals = Errors.Totals(errorRows);
            appErrors = totals.Where(kv => Apps.AppWorkload(kv.Key)).Sum(kv => kv.Value);
            platformErrors = totals.Values.Sum() - appErrors;
        }

        CostsView? costs = null;
        if (costRows is not null)
        {
            var all = Costs.Parse(costRows);
            costs = Costs.Build(all, span, now, settings.Budget, CostEnd(all, now));
        }

        var model = new HomePage
        {
            Page = "home",
            Days = span,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            AppCount = Apps.All.Count,
            Deploys = deploys,
            SuccessPct = deploys is null ? null : Deployments.Rate(deploys.Count(d => d.Succeeded), deploys.Count),
            AppsNow = new Headline(appProblems, appErrors),
            PlatformNow = new Headline(platformProblems, platformErrors),
            Costs = costs,
            SloTally = Slo.Tally(SloReports(got, now, Apps.All)),
            SloWindow = Slo.WindowDays,
        };
        return View("Home", model);
    }

    // The estate-wide views this replaced, kept as redirects so bookmarks
    // land somewhere: deploys are all apps', ops mostly the platform's, and
    // costs the whole subscription's. One action each, so whatever spelling
    // routing matches (a trailing slash, another case) has its target.
    [HttpGet("/deployments")]
    public IActionResult OldDeployments(string? days) => new SeeOther($"/apps?days={Queries.Range(days)}");

    [HttpGet("/ops")]
    public IActionResult OldOps(string? days) => new SeeOther($"/platform?days={Queries.Range(days)}");

    [HttpGet("/costs")]
    public IActionResult OldCosts(string? days) => new SeeOther($"/overall?days={Queries.Range(days)}");

    [HttpGet("/apps")]
    public async Task<IActionResult> AppsIndex(string? days, CancellationToken cancellationToken)
    {
        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var got = await queries.GatherAsync(queries.IndexQueries(span, now), cancellationToken).ConfigureAwait(false);
        if (got["deploys"].Failure is { } failure)
        {
            return Failed("apps", span, failure);
        }

        var deploys = Apps.ParseDeploys(got["deploys"].Rows!);
        var opsRows = queries.Ok(got["ops"], "ops");
        var costRows = queries.Ok(got["costs"], "costs");
        var errorRows = queries.Ok(got["errors"], "errors");
        var current = opsRows is null ? null : Ops.Latest(Ops.Parse(opsRows), now).Values.ToList();
        var totals = errorRows is null ? null : Errors.Totals(errorRows);
        var allCosts = costRows is null ? null : Costs.Parse(costRows);
        DateOnly? end = allCosts is null ? null : CostEnd(allCosts, now);
        var since = Deployments.RangeStart(span, now);
        var summaries = Apps.All
            .Select(app => Apps.Summarise(
                app,
                deploys,
                since,
                current,
                allCosts is not null && end is { } last ? Costs.Spend(Apps.CostsFor(app, allCosts), last, span) : null,
                totals))
            .ToList();
        // Every deploy is an app's (only container-apps-deploy writes the
        // table), so the estate's deploy panels live here, under the cards.
        var view = Deployments.Build(deploys, span, now);
        var model = new AppsPage
        {
            Page = "apps",
            Days = span,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            Summaries = summaries,
            Symbol = allCosts is { Count: > 0 } ? Costs.Symbol(Costs.BillingCurrency(allCosts)) : "",
            Now = now,
            Deployments = view,
            PayloadJson = view.Total > 0 ? Serialize(Payload(new() { ["deploy"] = view.Charts }, now, span)) : null,
        };
        return View("Apps", model);
    }

    [HttpGet("/apps/{slug}")]
    public async Task<IActionResult> AppPage(string slug, string? days, CancellationToken cancellationToken)
    {
        if (Apps.Find(slug) is not { } app)
        {
            return NotFound();
        }

        if (slug != app.Slug)
        {
            return ToSlug("/apps", app);
        }

        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var got = await queries.GatherAsync(queries.AppQueries(span, now), cancellationToken).ConfigureAwait(false);
        if (got["deploys"].Failure is { } failure)
        {
            return Failed("apps", span, failure);
        }

        var view = Deployments.Build(Apps.DeploysFor(app, Apps.ParseDeploys(got["deploys"].Rows!)), span, now);
        var panels = BuildPanels(
            got,
            es => Apps.EventsFor(app, es),
            rs => Apps.FailedRunsFor(app, rs),
            cs => Apps.CostsFor(app, cs),
            app.OwnsWorkload,
            span,
            now);
        var groups = new Dictionary<string, object> { ["deploy"] = view.Charts };
        foreach (var (key, value) in panels.Groups)
        {
            groups[key] = value;
        }

        var model = new AppPage
        {
            Page = "apps",
            Days = span,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            App = app,
            Deployments = view,
            Panels = panels,
            PayloadJson = Serialize(Payload(groups, now, span, panels.Costs?.Symbol ?? "")),
        };
        return View("App", model);
    }

    [HttpGet("/platform")]
    public async Task<IActionResult> Platform(string? days, CancellationToken cancellationToken)
    {
        // Everything no app owns. Every query here is optional: one that fails
        // renders a "couldn't read" card and the rest of the page is current.
        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var specs = queries.AppQueries(span, now);
        specs.Remove("deploys"); // no platform deploys are recorded
        var got = await queries.GatherAsync(specs, cancellationToken).ConfigureAwait(false);
        var panels = BuildPanels(
            got,
            Apps.PlatformEvents,
            Apps.PlatformFailedRuns,
            Apps.PlatformCosts,
            w => !Apps.AppWorkload(w),
            span,
            now);
        var model = new PlatformPage
        {
            Page = "platform",
            Days = span,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            Panels = panels,
            Silent = panels.Ops is null ? [] : Silent(panels.Ops.Sections.Select(s => s.Kind)),
            PayloadJson = panels.Groups.Count > 0 ? Serialize(Payload(panels.Groups, now, span, panels.Costs?.Symbol ?? "")) : null,
        };
        return View("Platform", model);
    }

    [HttpGet("/overall")]
    public async Task<IActionResult> Overall(string? days, CancellationToken cancellationToken)
    {
        // The whole subscription's costs: the budget, and every resource
        // group's spend broken down by area, group and service. The same query
        // and window as the other pages, so they share the cache.
        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var spec = queries.AppQueries(span, now)["costs"];
        var got = (await queries.GatherAsync(new Dictionary<string, (string, TimeSpan)> { ["costs"] = spec }, cancellationToken).ConfigureAwait(false))["costs"];
        if (got.Failure is { } failure)
        {
            return Failed("overall", span, failure);
        }

        var all = Costs.Parse(got.Rows!);
        // The same end as the app and platform pages, so a range covers the same days.
        var view = Costs.Build(all, span, now, settings.Budget, CostEnd(all, now));
        var model = new OverallPage
        {
            Page = "overall",
            Days = span,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            Costs = view,
            PayloadJson = Serialize(Payload(new() { ["costs"] = view.Charts }, now, span, view.Symbol)),
        };
        return View("Overall", model);
    }

    [HttpGet("/slo")]
    public async Task<IActionResult> SloIndex(string? days, CancellationToken cancellationToken)
    {
        // A tile per app: its worst SLO status and the budget it has least of.
        // The window is fixed, so the range only carries through the links.
        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var got = await queries.GatherAsync(queries.SloPageQueries(span), cancellationToken).ConfigureAwait(false);
        var model = new SloIndexPage
        {
            Page = "slo",
            Days = span,
            ShowRange = false,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            Reports = SloReports(got, now, Apps.All),
            WindowDays = Slo.WindowDays,
        };
        return View("Slo", model);
    }

    [HttpGet("/slo/{slug}")]
    public async Task<IActionResult> SloApp(string slug, string? days, CancellationToken cancellationToken)
    {
        // One app's SLIs in full: each against its target, its error budget,
        // burn rates and the budget's burn-down over the window.
        if (Apps.Find(slug) is not { } app)
        {
            return NotFound();
        }

        if (slug != app.Slug)
        {
            return ToSlug("/slo", app);
        }

        var span = Queries.Range(days);
        var now = clock.GetUtcNow();
        var got = await queries.GatherAsync(queries.SloPageQueries(span), cancellationToken).ConfigureAwait(false);
        var report = SloReports(got, now, [app])[0];
        var charts = report.Charts;
        var model = new SloAppPage
        {
            Page = "slo",
            Days = span,
            ShowRange = false,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            App = app,
            Report = report,
            WindowDays = Slo.WindowDays,
            PayloadJson = charts.Burndown.Count > 0
                ? Serialize(new Dictionary<string, object> { ["groups"] = new Dictionary<string, object> { ["slo"] = charts }, ["now"] = Times.JsTime(now) })
                : null,
        };
        return View("SloApp", model);
    }

    // --- shared ------------------------------------------------------------

    private static readonly IReadOnlyList<string> Slugs = [.. Apps.All.Select(a => a.Slug)];

    private static Dictionary<string, object> Payload(Dictionary<string, object> groups, DateTimeOffset now, int days, string? symbol = null)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["groups"] = groups,
            ["now"] = Times.JsTime(now),
            ["days"] = days,
        };
        if (symbol is not null)
        {
            payload["symbol"] = symbol;
        }

        return payload;
    }

    /// <summary>The always-reporting checks with no current row, by title, given the kinds of the platform's current rows.</summary>
    public static List<string> Silent(IEnumerable<string> kinds)
    {
        var present = kinds.ToHashSet(StringComparer.Ordinal);
        return [.. Ops.Sections.Where(s => AlwaysReport.Contains(s.Kind) && !present.Contains(s.Kind)).Select(s => s.Title)];
    }

    /// <summary>
    /// The estate's last day with data, not the app's, so an app with no
    /// spend for a few days isn't shown an older range.
    /// </summary>
    private static DateOnly CostEnd(IReadOnlyList<Cost> all, DateTimeOffset now) => Costs.LastDay(all, Times.Day(now));

    /// <summary>
    /// Every app's SLO report from the <c>deploys</c>, <c>http</c> and
    /// <c>jobs</c> results. Each query is optional: a failed one makes the
    /// SLIs it feeds read "unavailable" rather than failing the page.
    /// </summary>
    private List<SloReport> SloReports(Dictionary<string, Result> got, DateTimeOffset now, IReadOnlyList<App> which)
    {
        var deployRows = queries.Ok(got["deploys"], "deploys");
        var httpRows = queries.Ok(got["http"], "http-logs");
        var jobRows = queries.Ok(got["jobs"], "job-runs");
        var deploys = deployRows is null ? null : Apps.ParseDeploys(deployRows);
        var hours = httpRows is null ? null : Slo.ParseHttp(httpRows);
        var runs = jobRows is null ? null : Slo.ParseJobs(jobRows);
        return [.. which.Select(a => Apps.SloReportFor(a, hours, runs, deploys, now))];
    }

    /// <summary>
    /// The ops, costs and errors panels shared by an app's page and the
    /// platform's, over the rows the slice keeps. A failed query leaves its
    /// view null, and its card says so.
    /// </summary>
    private Panels BuildPanels(
        Dictionary<string, Result> got,
        Func<List<OpsEvent>, List<OpsEvent>> events,
        Func<List<FailedRun>?, List<FailedRun>?> runs,
        Func<List<Cost>, List<Cost>> costs,
        Func<string, bool> workload,
        int span,
        DateTimeOffset now)
    {
        var opsRows = queries.Ok(got["ops"], "ops");
        var failedRows = queries.Ok(got["failed"], "failed-runs");
        OpsView? ops = null;
        if (opsRows is not null)
        {
            var failed = failedRows is null ? null : Ops.ParseFailedRuns(failedRows);
            ops = Ops.Build(events(Ops.Parse(opsRows)), runs(failed), span, now);
        }

        var costRows = queries.Ok(got["costs"], "costs");
        CostsView? costView = null;
        if (costRows is not null)
        {
            var all = Costs.Parse(costRows);
            // Only the slice's rows, stacked by its own services: the budget
            // and the estate's areas are the Overall page's.
            costView = Costs.Build(costs(all), span, now, settings.Budget, CostEnd(all, now), byArea: false);
        }

        var errorRows = queries.Ok(got["errors"], "errors");
        List<ErrorGroup>? errorGroups = null;
        var errorTotal = 0;
        if (errorRows is not null)
        {
            var since = Deployments.RangeStart(span, now);
            errorGroups = Errors.Group(Errors.Parse(errorRows).Where(e => workload(e.Workload) && e.When >= since));
            errorTotal = Errors.Totals(errorRows).Where(kv => workload(kv.Key)).Sum(kv => kv.Value);
        }

        var groups = new Dictionary<string, object>(StringComparer.Ordinal);
        if (ops is not null)
        {
            groups["ops"] = ops.Charts;
        }

        if (costView is not null)
        {
            groups["costs"] = costView.Charts;
        }

        return new Panels
        {
            Ops = ops,
            Costs = costView,
            Errors = errorGroups,
            ErrorTotal = errorTotal,
            Now = now,
            Groups = groups,
        };
    }

    /// <summary>A former name (<see cref="App.Former"/>) to the app's own URL, query kept.</summary>
    private SeeOther ToSlug(string page, App app) => new($"{page}/{app.Slug}{Request.QueryString}");

    private ViewResult Failed(string page, int days, QueryFailedException failure)
    {
        var view = View("Error", new ErrorPage
        {
            Page = page,
            Days = days,
            Demo = settings.UseDemo,
            Environment = settings.Environment,
            Release = Release(),
            AppSlugs = Slugs,
            Error = failure.Message,
        });
        view.StatusCode = StatusCodes.Status502BadGateway;
        return view;
    }
}
