using System.Globalization;

namespace Obsview;

/// <summary>
/// Configuration, read from the environment (and locally from <c>.env</c>).
/// Every name is the one <c>make deploy</c> sets, so the deployment needs no
/// change for the .NET app. Anything here except the passcode is safe in a
/// log line; the passcode reaches the container as the Container Apps secret
/// <c>passcode</c>, mapped to <c>APP_PASSCODE</c>. There is no Key Vault by
/// decision (CLAUDE.md).
/// </summary>
public sealed record Settings
{
    /// <summary>
    /// Which <c>Environment</c> value the tables are filtered to. It goes
    /// into KQL, so it is held to a plain slug rather than escaped at every use.
    /// </summary>
    public string Environment { get; init; } = "dev";

    /// <summary>
    /// The Log Analytics workspace's customer ID (a GUID), not its resource
    /// ID. Empty means "no Azure": the app serves demo data, under a banner.
    /// </summary>
    public string WorkspaceId { get; init; } = "";

    /// <summary>
    /// The user-assigned identity's client id. Empty locally, which is the
    /// signal to fall back to DefaultAzureCredential (your <c>az login</c>).
    /// </summary>
    public string AzureClientId { get; init; } = "";

    /// <summary>Serve generated data even with a workspace configured.</summary>
    public bool Demo { get; init; }

    /// <summary>
    /// The monthly budget the costs views measure against: the Visual Studio
    /// subscription's credit, in the cost feed's currency.
    /// </summary>
    public double Budget { get; init; } = 150.0;

    /// <summary>
    /// Query results are kept this long. The tables are written by daily jobs
    /// and by deploys, so five minutes stale costs nothing and makes every
    /// open after the first instant.
    /// </summary>
    public int CacheTtlSeconds { get; init; } = 300;

    /// <summary>
    /// Where the vendored chart library lives. The image puts it at
    /// /app/vendor; locally <c>make app-vendor</c> fetches it into app/vendor.
    /// </summary>
    public string VendorDir { get; init; } = "vendor";

    /// <summary>The gate defaults on, so forgetting to configure it fails closed.</summary>
    public bool RequirePasscode { get; init; } = true;

    /// <summary>
    /// Signs the session cookie. Unset means it is derived from the passcode,
    /// so a deploy or a new replica does not log anyone out.
    /// </summary>
    public string CookieSecret { get; init; } = "";

    public int CookieMaxAgeSeconds { get; init; } = 60 * 60 * 24 * 30;

    /// <summary>The passcode, or empty when none is configured (nobody signs in).</summary>
    public string Passcode { get; init; } = "";

    public bool UseDemo => Demo || string.IsNullOrEmpty(WorkspaceId);

    /// <summary>
    /// Settings from configuration: environment variables, then <c>.env</c>
    /// (see <see cref="DotEnv"/>). A malformed value fails at start-up rather
    /// than reading as a default.
    /// </summary>
    public static Settings From(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var defaults = new Settings();
        var environment = Text(config, "ENVIRONMENT", defaults.Environment);
        if (environment.Length == 0 || !environment.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new InvalidOperationException("ENVIRONMENT must be letters, digits and hyphens");
        }

        return new Settings
        {
            Environment = environment,
            WorkspaceId = Text(config, "WORKSPACE_ID", ""),
            AzureClientId = Text(config, "AZURE_CLIENT_ID", ""),
            Demo = Flag(config, "DEMO", defaults.Demo),
            Budget = Number(config, "BUDGET", defaults.Budget),
            CacheTtlSeconds = (int)Number(config, "CACHE_TTL_SECONDS", defaults.CacheTtlSeconds),
            VendorDir = Text(config, "VENDOR_DIR", defaults.VendorDir),
            RequirePasscode = Flag(config, "REQUIRE_PASSCODE", defaults.RequirePasscode),
            CookieSecret = Text(config, "COOKIE_SECRET", ""),
            CookieMaxAgeSeconds = (int)Number(config, "COOKIE_MAX_AGE_SECONDS", defaults.CookieMaxAgeSeconds),
            Passcode = Text(config, "APP_PASSCODE", ""),
        };
    }

    private static string Text(IConfiguration config, string key, string fallback) =>
        config[key] is { } value ? value.Trim() : fallback;

    private static bool Flag(IConfiguration config, string key, bool fallback)
    {
        var value = config[key]?.Trim().ToLowerInvariant();
        return value switch
        {
            null or "" => fallback,
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException($"{key} must be true or false, not '{value}'"),
        };
    }

    private static double Number(IConfiguration config, string key, double fallback)
    {
        var value = config[key]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new InvalidOperationException($"{key} must be a number, not '{value}'");
    }
}

/// <summary>
/// Reads <c>KEY=value</c> lines from a <c>.env</c> file, for a local run.
/// Lower priority than the environment, so a real variable always wins.
/// Blank lines and <c>#</c> comments are skipped; one pair of matching quotes
/// round a value is removed.
/// </summary>
public static class DotEnv
{
    public static Dictionary<string, string?> Read(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
