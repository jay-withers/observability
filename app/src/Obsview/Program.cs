using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.WebEncoders;
using Obsview;
using Obsview.Web;

var builder = WebApplication.CreateBuilder(args);

// The app's own settings: a local run's `.env` (OBSVIEW_ENV_FILE, which
// `make app-run` points at app/.env, or ./.env), then the environment, which
// always wins. A configuration of their own, not the host's: the host's
// strips the ASPNETCORE_ and DOTNET_ prefixes, so its "ENVIRONMENT" would be
// ASPNETCORE_ENVIRONMENT when ENVIRONMENT is unset, and the KQL would filter
// on "Development".
var appConfig = new ConfigurationBuilder()
    .Add(new MemoryConfigurationSource { InitialData = DotEnv.Read(Environment.GetEnvironmentVariable("OBSVIEW_ENV_FILE") ?? ".env") })
    .AddEnvironmentVariables()
    .Build();

builder.Services.AddSingleton(_ => Settings.From(appConfig));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Session>();
builder.Services.AddSingleton<Queries>();
builder.Services.AddSingleton<ILogsSource>(services => services.GetRequiredService<Settings>() is var s && s.UseDemo
    ? new DemoLogs()
    : new CachedLogs(
        new AzureLogs(s.WorkspaceId, s.AzureClientId, services.GetRequiredService<ILogger<AzureLogs>>()),
        TimeSpan.FromSeconds(s.CacheTtlSeconds)));
builder.Services.AddHostedService<Warmer>();
builder.Services.AddControllersWithViews();
// Nothing here is protected across restarts (the session and CSRF cookies are
// signed by their own keys; MVC's data protection is only registered, never
// used), so the framework's key ring needn't outlive the process, nor warn at
// every start that it won't.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
// Text is written as itself, not as character references: "–" and "›" are
// display text, and escaping them would only make the HTML harder to read.
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

// $PORT, as `make deploy` sets it and the ingress targets (8000 by default).
var port = appConfig["PORT"] is { Length: > 0 } p ? p : "8000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();
// Resolved from the container, not read here, so a test host's settings are the ones every endpoint sees.
var settings = app.Services.GetRequiredService<Settings>();
var startup = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("obsview");
if (settings.RequirePasscode && settings.Passcode.Length == 0)
{
    // Fails closed anyway (no passcode matches an empty one), but say so.
    Log.NoPasscode(startup);
}

// Outermost, so the headers land on everything; then the cross-site refusal,
// before any page sees an unsafe request.
// The ingress's X-Forwarded-For and -Proto, trusted from any address as the
// Python app's server did (forwarded_allow_ips="*"): only the Container Apps
// ingress can reach the replica.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);
app.UseSecurityHeaders((appConfig["CSP_FORM_ACTION"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
app.UseCsrf();
app.UseStaticFiles();
var vendor = Path.GetFullPath(settings.VendorDir);
if (Directory.Exists(vendor))
{
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(vendor), RequestPath = "/vendor" });
}
else
{
    Log.NoVendor(startup, vendor);
}

// Answers from memory alone, so the liveness probe never restarts a replica
// because Log Analytics was slow. It reports the image tag, which is what the
// shared smoke test checks.
app.MapGet("/healthz", (Settings settings) => Results.Json(new Dictionary<string, string>
{
    ["status"] = "ok",
    ["environment"] = settings.Environment,
    ["version"] = Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "unknown",
}));

// Can this replica read the workspace? The shared smoke test checks it after
// every deploy, so a wrong WORKSPACE_ID or a missing role fails the deploy
// (and rolls it back) instead of the first page load. Not the readiness
// probe: a slow workspace must not take the app out of rotation.
app.MapGet("/readyz", async (Settings settings, Queries queries, ILogsSource source, CancellationToken cancellationToken) =>
{
    if (settings.UseDemo && !settings.Demo)
    {
        // Demo data in Azure is a misconfiguration, not a feature.
        return Results.Json(new Dictionary<string, object> { ["status"] = "unavailable", ["workspace"] = false, ["reason"] = "no WORKSPACE_ID" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var (kql, window) = queries.ReadyQuery();
        await source.QueryAsync(kql, window, cancellationToken).ConfigureAwait(false);
    }
    catch (QueryFailedException exc)
    {
        return Results.Json(new Dictionary<string, object> { ["status"] = "unavailable", ["workspace"] = false, ["reason"] = exc.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Json(new Dictionary<string, object> { ["status"] = "ok", ["workspace"] = true });
});

app.MapGet("/manifest.json", () => Results.Json(new Dictionary<string, object>
{
    ["name"] = "Observability",
    ["short_name"] = "Obs",
    ["start_url"] = "/",
    ["display"] = "standalone",
    ["background_color"] = "#0e1116",
    ["theme_color"] = "#0e1116",
    ["icons"] = new[] { new Dictionary<string, string> { ["src"] = "/static/icon.svg", ["sizes"] = "any", ["type"] = "image/svg+xml" } },
}));

app.MapControllers();
await app.RunAsync().ConfigureAwait(false);

/// <summary>The entry point, public so the route tests can host it.</summary>
public partial class Program
{
    protected Program()
    {
    }
}

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "APP_PASSCODE is not set: nobody can sign in")]
    public static partial void NoPasscode(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "no {Vendor} directory: charts will not draw (make app-vendor)")]
    public static partial void NoVendor(ILogger logger, string vendor);
}
