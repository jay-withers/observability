using System.Net;
using Obsview.Web;

namespace Obsview.Tests;

/// <summary>
/// The CSRF defences through obsview's own login route: the <c>Origin</c>
/// fallback for a browser without <c>Sec-Fetch-Site</c>, and
/// <see cref="Csrf.TokenIn"/> on a page with no field.
/// </summary>
public sealed class CsrfTests
{
    [Theory]
    [InlineData("https://testserver", HttpStatusCode.SeeOther)]
    [InlineData("https://testserver:443", HttpStatusCode.SeeOther)]
    [InlineData("https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    public async Task Without_sec_fetch_site_the_origin_decides(string origin, HttpStatusCode status)
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await TestApp.LogIn(client, TestApp.Passcode, new Dictionary<string, string> { ["Origin"] = origin });
        Assert.Equal(status, response.StatusCode);
    }

    [Theory]
    [InlineData("same-origin", HttpStatusCode.SeeOther)]
    [InlineData("none", HttpStatusCode.SeeOther)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    public async Task Sec_fetch_site_decides_when_sent(string site, HttpStatusCode status)
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var response = await TestApp.LogIn(client, TestApp.Passcode, new Dictionary<string, string> { ["Sec-Fetch-Site"] = site, ["Origin"] = "https://testserver" });
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public void Token_in_says_when_the_page_has_no_field()
    {
        var error = Assert.Throws<ArgumentException>(() => Csrf.TokenIn("<p>no form</p>"));
        Assert.Contains("csrf_token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_junk_cookie_is_replaced_never_echoed()
    {
        using var app = new TestApp();
        using var client = app.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Add("Cookie", "__Host-csrf=<script>");
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", Csrf.TokenIn(html), StringComparison.Ordinal);
        Assert.Equal(43, Csrf.TokenIn(html).Length);
    }
}
