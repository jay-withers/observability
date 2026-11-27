using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Obsview.Web;

namespace Obsview.Tests;

public class SettingsTests
{
    private static Settings From(params (string Key, string? Value)[] values) =>
        Settings.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void The_defaults_fail_closed_and_serve_the_demo()
    {
        var settings = From();
        Assert.Equal("dev", settings.Environment);
        Assert.True(settings.RequirePasscode);
        Assert.True(settings.UseDemo);
        Assert.Equal(150.0, settings.Budget);
        Assert.Equal(300, settings.CacheTtlSeconds);
        Assert.Equal("", settings.Passcode);
    }

    [Fact]
    public void Every_variable_make_deploy_sets_is_read()
    {
        var settings = From(
            ("ENVIRONMENT", "prod-2"),
            ("WORKSPACE_ID", " 0000-1111 "),
            ("AZURE_CLIENT_ID", "client"),
            ("APP_PASSCODE", "sesame"),
            ("BUDGET", "99.5"),
            ("CACHE_TTL_SECONDS", "60"),
            ("VENDOR_DIR", "/app/vendor"),
            ("REQUIRE_PASSCODE", "false"),
            ("COOKIE_SECRET", "k"),
            ("COOKIE_MAX_AGE_SECONDS", "10"),
            ("DEMO", "yes"));
        Assert.Equal(
            new Settings
            {
                Environment = "prod-2",
                WorkspaceId = "0000-1111",
                AzureClientId = "client",
                Passcode = "sesame",
                Budget = 99.5,
                CacheTtlSeconds = 60,
                VendorDir = "/app/vendor",
                RequirePasscode = false,
                CookieSecret = "k",
                CookieMaxAgeSeconds = 10,
                Demo = true,
            },
            settings);
        Assert.True(settings.UseDemo);
        Assert.False((settings with { Demo = false }).UseDemo);
    }

    [Theory]
    [InlineData("ENVIRONMENT", "dev\"; drop")]
    [InlineData("ENVIRONMENT", "")]
    [InlineData("DEMO", "maybe")]
    [InlineData("BUDGET", "lots")]
    public void A_malformed_value_fails_at_start_rather_than_reading_as_a_default(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => From((key, value)));

    [Fact]
    public void Dot_env_reads_pairs_comments_quotes_and_export()
    {
        var path = Path.Combine(Path.GetTempPath(), $"obsview-{Guid.NewGuid():N}.env");
        File.WriteAllLines(path, ["# a comment", "", "APP_PASSCODE=\"sesame\"", "export WORKSPACE_ID='w'", "BUDGET = 12", "not a pair", "=nokey"]);
        try
        {
            var values = DotEnv.Read(path);
            Assert.Equal(new Dictionary<string, string?> { ["APP_PASSCODE"] = "sesame", ["WORKSPACE_ID"] = "w", ["BUDGET"] = "12" }, values);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_dot_env_is_no_values() => Assert.Empty(DotEnv.Read("/nonexistent/.env"));

    [Fact]
    public void A_query_failure_keeps_its_cause()
    {
        var cause = new InvalidOperationException("inner");
        Assert.Same(cause, new QueryFailedException("outer", cause).InnerException);
        Assert.NotNull(new QueryFailedException().Message);
    }

    [Fact]
    public async Task An_unhandled_error_is_a_plain_500_that_still_carries_the_headers()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseSecurityHeaders(["https://access.example"]);
        app.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException("boom")));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/boom", UriKind.Relative));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Internal Server Error", await response.Content.ReadAsStringAsync());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self' https://access.example", csp, StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        await app.StopAsync();
    }
}
