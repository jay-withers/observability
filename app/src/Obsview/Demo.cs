using System.Globalization;

namespace Obsview;

/// <summary>
/// Generated rows, for running the app with no Azure at all. A local run
/// without a <c>WORKSPACE_ID</c> serves these, under a "demo data" banner.
/// They follow the real tables' columns and roughly their shape (a few
/// deploys a day, mostly Renovate, the odd rollback), seeded so a screenshot
/// is the same twice. Each query is recognised by the table its KQL reads.
/// </summary>
public sealed class DemoLogs(DateTimeOffset? now = null, int seed = 7) : ILogsSource
{
    private static readonly (string Repo, string App)[] DeployApps =
    [
        ("health", "health"),
        ("finances", "finances"),
        ("career", "career"),
        ("observability", "obsview"),
        ("repo-agent", "repoagent"),
        ("security-testing", "scanner"),
        ("market-agent", "api"),
    ];

    private static readonly string[] Jobs =
    [
        "caj-gymlog-dev-garmin",
        "caj-gymlog-dev-insight",
        "caj-gymlog-dev-backup",
        "caj-finances-dev-backup",
        "caj-finances-dev-digest",
        "caj-career-dev-backup",
        "caj-marketagent-dev-daily-summary",
        "caj-repoagent-dev-scan",
        "caj-sectest-dev-zap",
    ];

    private static readonly string[] Roots =
    [
        "azure-landingzone/hub",
        "azure-container-apps/terraform",
        "cloudflare/terraform",
        "github-repos/terraform",
        "health/terraform",
        "finances/terraform",
        "career/terraform",
        "market-agent/terraform",
        "observability/terraform",
    ];

    private static readonly string[] Hosts =
    [
        "health.jaywithers.uk",
        "finances.jaywithers.uk",
        "marketagent.jaywithers.uk",
        "observability.jaywithers.uk",
    ];

    private static readonly string[] Sessions =
    [
        "firewall", "aks", "vm", "bastion", "vpn-gateway", "app-gateway", "nat-gateway", "workload-profile",
    ];

    private static readonly (string Group, string Service, string Name, double Base)[] CostShape =
    [
        ("rg-platform-dev", "Log Analytics", "log-platform-dev", 0.55),
        ("rg-platform-dev", "Azure Container Apps", "cae-platform-dev", 0.20),
        ("rg-platform-dev", "Storage", "stplatformdev", 0.04),
        ("rg-gymlog-dev", "Storage", "stgymlogdev", 0.02),
        ("rg-gymlog-dev", "Key Vault", "kv-gymlog-dev", 0.01),
        ("rg-finances-dev", "Azure Database for PostgreSQL", "psql-finances-dev", 0.42),
        ("rg-career-dev", "Storage", "stcareerdev", 0.02),
        ("rg-marketagent-dev", "Azure Container Apps", "ca-marketagent-dev", 0.31),
        ("rg-marketagent-dev", "Storage", "stmarketagentdev", 0.05),
        ("rg-aks-dev", "Azure Kubernetes Service", "aks-dev", 0.0),
        ("rg-hub-dev", "Azure Bastion", "bas-hub-dev", 0.0),
        ("rg-hub-dev", "Virtual Network", "vnet-hub-dev", 0.03),
    ];

    private static readonly (string Workload, string Message, double Chance)[] ConsoleErrors =
    [
        ("ca-gymlog-dev", "ERROR:    Exception in ASGI application", 0.6),
        ("ca-gymlog-dev", "INFO:     10.0.0.4:5123 - \"GET /sessions/{n} HTTP/1.1\" 500 Internal Server Error", 0.5),
        ("caj-gymlog-dev-garmin", "ERROR garmin: login failed (HTTP 429), retrying in {n}s", 0.4),
        ("ca-finances-dev", "ERROR sqlalchemy.pool: connection {n} was closed by the server", 0.2),
        ("ca-marketagent-dev", "CRITICAL risk: order {n} rejected: position limit exceeded", 0.15),
        ("caj-repoagent-dev-scan", "ERROR github: 502 Bad Gateway from api.github.com", 0.3),
    ];

    /// <summary>
    /// Workloads with ingress, their requests an hour at most, and their
    /// hourly chance of a 5xx and of a slow request. Finances had a bad
    /// evening four days ago (its availability budget is at risk),
    /// market-agent's api is slow (latency budget spent), and the rest are healthy.
    /// </summary>
    private static readonly (string Workload, int Most, double ErrorRate, double SlowRate)[] Web =
    [
        ("ca-gymlog-dev", 40, 0.002, 0.02),
        ("ca-finances-dev", 25, 0.001, 0.02),
        ("ca-career-dev", 10, 0.0, 0.03),
        ("ca-marketagent-dev-api", 30, 0.001, 0.09),
        ("ca-marketagent-dev-dashboard", 15, 0.0, 0.03),
        ("ca-observability-dev", 12, 0.0, 0.03),
    ];

    public Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kql);
        var at = now ?? DateTimeOffset.UtcNow;
        var first = kql.TrimStart().Split('\n', 2)[0];
        var table = first.Split(' ', 2)[0];
        var rng = new Random(seed);
        IReadOnlyList<Row> rows;
        if (table == "OpsEvents_CL")
        {
            rows = OpsRows(at, timespan, rng);
        }
        // The Container Apps tables are read through a fuzzy union, so the
        // first word is `union`; the table is named later on the line.
        else if (first.Contains("ContainerAppHTTPLogs", StringComparison.Ordinal))
        {
            rows = HttpRows(at, timespan, rng);
        }
        // The job-runs query reads the same table as failed runs.
        else if (first.Contains("ContainerAppSystemLogs_CL", StringComparison.Ordinal))
        {
            rows = kql.Contains("SuccessfulCreate", StringComparison.Ordinal) ? JobRunRows(at, timespan, rng) : FailedRunRows(at);
        }
        else if (first.Contains("ContainerAppConsoleLogs_CL", StringComparison.Ordinal))
        {
            rows = ConsoleRows(at, timespan, rng);
        }
        // The costs query starts with a `let` over the table.
        else if (first.Contains("Costs_CL", StringComparison.Ordinal))
        {
            rows = CostRows(at, timespan, rng);
        }
        else
        {
            rows = table == "Deployments_CL" ? DeployRows(at, timespan, rng) : [];
        }

        return Task.FromResult(rows);
    }

    private static T Choose<T>(Random rng, T[] items, int[] weights)
    {
        var pick = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < items.Length; i++)
        {
            pick -= weights[i];
            if (pick < 0)
            {
                return items[i];
            }
        }

        return items[^1];
    }

    private static Row Make(params (string Key, object? Value)[] values)
    {
        var row = new Row();
        foreach (var (key, value) in values)
        {
            row[key] = value;
        }

        return row;
    }

    private static List<Row> DeployRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        var versions = DeployApps.ToDictionary(a => a.App, _ => new[] { 0, rng.Next(3, 21), 0 });
        // Generated from a fixed origin forwards, then cut to the window, so a
        // shorter range is a suffix of a longer one, as with real data.
        for (var day = now.AddDays(-365); day < now; day = day.AddDays(1))
        {
            foreach (var (repo, app) in DeployApps)
            {
                if (rng.NextDouble() > 0.35)
                {
                    continue;
                }

                var when = day.AddMinutes(rng.Next(0, 1440));
                if (when > now)
                {
                    continue;
                }

                var v = versions[app];
                var previous = $"v{v[0]}.{v[1]}.{v[2]}";
                v[2]++;
                var trigger = Choose(rng, ["renovate", "merge", "manual"], [6, 3, 1]);
                var outcome = Choose(rng, ["success", "untested", "rolled_back", "failed"], [85, 6, 5, 4]);
                rows.Add(Make(
                    ("TimeGenerated", when),
                    ("Repo", repo),
                    ("App", app),
                    ("Version", $"v{v[0]}.{v[1]}.{v[2]}"),
                    ("PreviousVersion", previous),
                    ("Outcome", outcome),
                    ("Trigger", trigger),
                    ("LeadTimeSec", trigger != "manual" ? (object)rng.Next(180, 2401) : null),
                    ("CommitSha", rng.NextInt64(0, 1L << 40).ToString("x10", CultureInfo.InvariantCulture)),
                    ("RunUrl", $"https://github.com/jay-withers/{repo}/actions")));
            }
        }

        var cutoff = now - timespan;
        return [.. rows.Where(r => (DateTimeOffset)r["TimeGenerated"]! >= cutoff).OrderByDescending(r => (DateTimeOffset)r["TimeGenerated"]!)];
    }

    private static List<Row> OpsRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        var days = Math.Max(1, timespan.Days);
        for (var back = days - 1; back >= 0; back--)
        {
            var day = now.AddDays(-back).UtcDateTime;
            var when = new DateTimeOffset(day.Year, day.Month, day.Day, 6, 0, 0, TimeSpan.Zero);
            if (when > now)
            {
                when = now.AddMinutes(-5);
            }

            void Add(string kind, string name, string status, string detail) => rows.Add(Make(
                ("TimeGenerated", when),
                ("Kind", kind),
                ("Name", name),
                ("Status", status),
                ("Detail", detail),
                ("RunUrl", "https://github.com/jay-withers/observability/actions"),
                ("Repo", "observability")));

            foreach (var job in Jobs)
            {
                var late = job == "caj-sectest-dev-zap" && back < 2;
                Add("heartbeat", job, late ? "overdue" : "ok", late ? "Last success 2 days ago. Overdue after 36h." : "Last success today.");
            }

            foreach (var check in new[] { "cost-feed", "expiry", "session" })
            {
                Add("heartbeat", $"check: {check}", "ok", "Last row today.");
            }

            foreach (var root in Roots)
            {
                var drifted = root == "cloudflare/terraform" && back is 3 or 4 or 5;
                var status = drifted ? "drifted" : rng.NextDouble() < 0.02 ? "failed" : "ok";
                Add("drift", root, status, status switch
                {
                    "drifted" => "2 to change.",
                    "failed" => "Plan failed: see the run.",
                    _ => "No changes.",
                });
            }

            for (var i = 0; i < Hosts.Length; i++)
            {
                var left = i > 0 ? 74 - back + (i * 9) : 19 - (back / 4);
                var expires = Times.Iso(Times.Day(now.AddDays(left)));
                Add("expiry", Hosts[i], left < 21 ? "expiring" : "ok", $"Certificate expires {expires} ({left} days).");
            }

            foreach (var category in Sessions)
            {
                var running = category == "bastion" && back == 0;
                Add("session", category, running ? "running" : "ok", running
                    ? "Still running: rg-hub-dev/bas-hub. Tag session-until=YYYY-MM-DD to keep it."
                    : "Nothing running.");
            }

            Add("cost-feed", "Costs_CL", "ok", "Posted 184 Costs_CL row(s).");
        }

        return rows;
    }

    private static List<Row> FailedRunRows(DateTimeOffset now) =>
    [
        Make(("Job", "caj-sectest-dev-zap"), ("Reason", "BackoffLimitExceeded"), ("Failures", 3L), ("LastFailure", now.AddHours(-30)), ("Sample", "Job has reached the specified backoff limit")),
        Make(("Job", "ca-finances-dev"), ("Reason", "ContainerCrashing"), ("Failures", 2L), ("LastFailure", now.AddDays(-2)), ("Sample", "Container 'finances' was terminated with exit code 3")),
        Make(("Job", "caj-gymlog-dev-garmin"), ("Reason", "ContainerCrashing"), ("Failures", 1L), ("LastFailure", now.AddDays(-4)), ("Sample", "Container 'gymlog' was terminated with exit code 1")),
    ];

    private static List<Row> CostRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        var today = Times.Day(now);
        // The feed lags a day: yesterday is the newest usage date.
        for (var back = 1; back < timespan.Days; back++)
        {
            var day = today.AddDays(-back);
            foreach (var (group, service, name, baseCost) in CostShape)
            {
                var cost = baseCost * (0.8 + (rng.NextDouble() * 0.45));
                if (group == "rg-aks-dev" && back % 9 is 2 or 3)
                {
                    cost = 3.5 + (rng.NextDouble() * 1.5); // a session left the cluster up
                }

                if (group == "rg-hub-dev" && service == "Azure Bastion" && back is 1 or 2)
                {
                    cost = 4.6;
                }

                if (cost <= 0)
                {
                    continue;
                }

                rows.Add(Make(
                    ("UsageDate", Times.Midnight(day)),
                    ("ResourceGroup", group),
                    ("ServiceName", service),
                    ("ResourceId", $"/subscriptions/demo/resourcegroups/{group}/providers/demo/{name}"),
                    ("Cost", Math.Round(cost, 4)),
                    ("Currency", "USD")));
            }
        }

        return rows;
    }

    private static List<Row> ConsoleRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        for (var back = 0; back < Math.Max(1, timespan.Days); back++)
        {
            foreach (var (workload, message, chance) in ConsoleErrors)
            {
                var tries = rng.Next(1, 4);
                for (var n = 0; n < tries; n++)
                {
                    if (rng.NextDouble() > chance)
                    {
                        continue;
                    }

                    var when = now.AddDays(-back).AddMinutes(-rng.Next(0, 1440));
                    rows.Add(Make(
                        ("TimeGenerated", when),
                        ("Workload", workload),
                        ("Log_s", message.Replace("{n}", rng.Next(1, 901).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))));
                }
            }
        }

        return [.. rows.OrderByDescending(r => (DateTimeOffset)r["TimeGenerated"]!)];
    }

    private static List<Row> HttpRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        var utc = now.UtcDateTime;
        var top = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        for (var back = (int)(timespan.TotalSeconds / 3600); back >= 0; back--)
        {
            var hour = top.AddHours(-back);
            foreach (var (workload, most, errorRate, slowRate) in Web)
            {
                var total = rng.Next(0, most + 1);
                if (total == 0)
                {
                    continue;
                }

                var errors = Enumerable.Range(0, total).Count(_ => rng.NextDouble() < errorRate);
                if (workload == "ca-finances-dev" && back >= (4 * 24) - 3 && back <= 4 * 24)
                {
                    errors = total / 2; // the bad evening
                }

                var slow = Enumerable.Range(0, total).Count(_ => rng.NextDouble() < slowRate);
                rows.Add(Make(
                    ("Hour", hour),
                    ("Workload", workload),
                    ("Total", (long)total),
                    ("Errors", (long)errors),
                    ("Timed", (long)total),
                    ("Fast", (long)(total - slow))));
            }
        }

        return rows;
    }

    /// <summary>
    /// A run of every job a day; the scanner failing its last two, as in the
    /// heartbeats and failed runs, and the Garmin sync the odd time.
    /// </summary>
    private static List<Row> JobRunRows(DateTimeOffset now, TimeSpan timespan, Random rng)
    {
        var rows = new List<Row>();
        for (var back = Math.Max(1, timespan.Days) - 1; back >= 0; back--)
        {
            for (var n = 0; n < Jobs.Length; n++)
            {
                var job = Jobs[n];
                var day = now.AddDays(-back).UtcDateTime;
                var started = new DateTimeOffset(day.Year, day.Month, day.Day, 2 + (n % 6), 0, 0, TimeSpan.Zero);
                if (started > now)
                {
                    continue;
                }

                var failed = (job == "caj-sectest-dev-zap" && back < 2) || (job == "caj-gymlog-dev-garmin" && rng.NextDouble() < 0.04);
                rows.Add(Make(
                    ("Job", job),
                    ("Execution", $"{job}-{rng.Next(0, 1 << 24):x6}"),
                    ("Started", started),
                    ("Completed", failed ? 0L : 1L),
                    ("Failed", failed ? 1L : 0L)));
            }
        }

        return rows;
    }
}
