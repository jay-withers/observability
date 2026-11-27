namespace Obsview.Web;

/// <summary>What every page's layout needs: the tab, the range and the banner.</summary>
public abstract class PageModel
{
    public string Page { get; init; } = "";

    public int Days { get; init; }

    public bool ShowRange { get; init; } = true;

    public bool Demo { get; init; }

    public string Environment { get; init; } = "";

    /// <summary>
    /// The release this revision runs, beside the app's name on every
    /// signed-in page: <c>IMAGE_TAG</c>, trimmed, empty when unset or blank (a
    /// local run), which shows nothing. Not a secret: /healthz reports it.
    /// </summary>
    public string Release { get; init; } = "";

    public IReadOnlyList<string> AppSlugs { get; init; } = [];

    public virtual string Title => "Observability";

    /// <summary>The chart payload, serialised for the page's JSON block; null when it draws no charts.</summary>
    public string? PayloadJson { get; init; }
}

public sealed class LoginPage : PageModel
{
    public string Error { get; init; } = "";

    public string CsrfToken { get; init; } = "";

    public override string Title => "Sign in · Observability";
}

public sealed class ErrorPage : PageModel
{
    public string Error { get; init; } = "";

    public override string Title => "Unavailable · Observability";
}

/// <summary>A home tile's figures: problems now and error lines over the range. Null means that query failed.</summary>
public sealed record Headline(int? Problems, int? Errors);

public sealed class HomePage : PageModel
{
    public int AppCount { get; init; }

    public IReadOnlyList<Deploy>? Deploys { get; init; }

    public double? SuccessPct { get; init; }

    public Headline AppsNow { get; init; } = new(null, null);

    public Headline PlatformNow { get; init; } = new(null, null);

    public CostsView? Costs { get; init; }

    public IReadOnlyDictionary<string, int> SloTally { get; init; } = new Dictionary<string, int>();

    public int SloWindow { get; init; }
}

public sealed class AppsPage : PageModel
{
    public IReadOnlyList<Summary> Summaries { get; init; } = [];

    public string Symbol { get; init; } = "";

    public DateTimeOffset Now { get; init; }

    public required DeploymentsView Deployments { get; init; }

    public override string Title => "Apps · Observability";
}

/// <summary>The ops, costs and errors panels an app's page and the platform's share.</summary>
public sealed class Panels
{
    public OpsView? Ops { get; init; }

    public CostsView? Costs { get; init; }

    public IReadOnlyList<ErrorGroup>? Errors { get; init; }

    public int ErrorTotal { get; init; }

    public DateTimeOffset Now { get; init; }

    public Dictionary<string, object> Groups { get; init; } = [];
}

public sealed class AppPage : PageModel
{
    public required App App { get; init; }

    public required DeploymentsView Deployments { get; init; }

    public required Panels Panels { get; init; }

    public override string Title => $"{App.Title} · Observability";
}

public sealed class PlatformPage : PageModel
{
    public required Panels Panels { get; init; }

    /// <summary>The always-reporting checks with no row in 48 hours, by title.</summary>
    public IReadOnlyList<string> Silent { get; init; } = [];

    public override string Title => "Platform · Observability";
}

public sealed class OverallPage : PageModel
{
    public required CostsView Costs { get; init; }

    public override string Title => "Overall · Observability";
}

public sealed class SloIndexPage : PageModel
{
    public IReadOnlyList<SloReport> Reports { get; init; } = [];

    public int WindowDays { get; init; }

    public override string Title => "SLOs · Observability";
}

public sealed class SloAppPage : PageModel
{
    public required App App { get; init; }

    public required SloReport Report { get; init; }

    public int WindowDays { get; init; }

    public override string Title => $"{App.Title} SLOs · Observability";
}

// --- partials ---------------------------------------------------------------

public sealed record DeploymentsPanel(DeploymentsView View, string Group, bool PerApp, int Days, IReadOnlyList<string> AppSlugs);

public sealed record OpsPanel(OpsView View, string Group, bool PerApp, int Days, IReadOnlyList<string> Silent);

/// <summary><paramref name="Overall"/> is the whole subscription: the budget card, areas and by-area bars.</summary>
public sealed record CostsPanel(CostsView View, string Group, bool Overall);

public sealed record ErrorsPanel(IReadOnlyList<ErrorGroup>? Errors, int ErrorTotal, DateTimeOffset Now, int Days);
