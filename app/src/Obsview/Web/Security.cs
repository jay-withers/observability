using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Obsview.Web;

/// <summary>
/// The passcode gate. Reachable from the public internet, so the gate
/// defaults on and a missing passcode fails closed. A shared passcode rather
/// than Entra sign-in, because this repo has no app registration by decision.
/// The cookie is signed, not encrypted: it carries only an issue time,
/// <c>issued.hexsignature</c>, the same scheme the Python app used, so a
/// session survives the cutover.
/// </summary>
public sealed class Session(Settings settings, TimeProvider clock)
{
    public const string CookieName = "obsview_session";

    /// <summary>
    /// Derived from the passcode when <c>COOKIE_SECRET</c> is unset, so a new
    /// replica does not log anyone out, and rotating the passcode does.
    /// </summary>
    private byte[] SigningKey() => settings.CookieSecret.Length > 0
        ? Encoding.UTF8.GetBytes(settings.CookieSecret)
        : SHA256.HashData(Encoding.UTF8.GetBytes(settings.Passcode));

    private string Sign(string issued) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(SigningKey(), Encoding.ASCII.GetBytes(issued)));

    public string Issue()
    {
        var issued = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{issued}.{Sign(issued)}";
    }

    public bool Valid(string? token)
    {
        // No passcode means nobody is let in, cookie or not. Without this the
        // signing key falls back to sha256(""), a public constant, and anyone
        // could forge a session.
        if (settings.Passcode.Length == 0 || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return false;
        }

        var issued = token[..dot];
        var signature = token[(dot + 1)..];
        // ASCII digits only: a "²" must read as invalid, never as an error.
        if (issued.Length == 0 || !issued.All(char.IsAsciiDigit) || !long.TryParse(issued, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(Sign(issued))))
        {
            return false;
        }

        return clock.GetUtcNow().ToUnixTimeSeconds() - seconds < settings.CookieMaxAgeSeconds;
    }

    /// <summary>Compared as bytes, in constant time, so a non-ASCII passcode (a £) works.</summary>
    public bool CheckPasscode(string presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        return settings.Passcode.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(settings.Passcode));
    }
}

/// <summary>
/// Cross-site request forgery defences: an origin check and a form token,
/// the semantics of appkit's <c>csrf.py</c> (v0.8.0), which the Python app
/// carried a copy of.
///
/// <b>The origin check covers every form and fetch.</b> Every unsafe request
/// (POST, PUT, PATCH, DELETE) a browser sends cross-site is refused with a
/// 403. Browsers say where a request came from in <c>Sec-Fetch-Site</c>,
/// which a page cannot forge or suppress. A browser too old to send it still
/// sends <c>Origin</c> on a cross-origin POST, and that is compared with
/// <c>Host</c> (host and port only: TLS ends at the ingress). A request with
/// neither passes: curl, a test client, which carry no ambient cookies worth
/// forging. "same-site" is refused too: every app is its own subdomain of one
/// site, and none should post into another.
///
/// <b>The form token is double-submit.</b> <see cref="Token"/> gives a page
/// the value of the <c>__Host-csrf</c> cookie, minting and setting one if the
/// browser has none, so a page without a form sets nothing. The form posts it
/// back as <c>csrf_token</c>, and <see cref="Valid"/> checks the field
/// matches the cookie. A forger can make a browser send the cookie but can't
/// read it to fill in the field. <c>__Host-</c> pins the cookie to this exact
/// origin, over HTTPS, at <c>/</c>. A refused token re-renders the form with a
/// fresh token, so a retry works.
/// </summary>
public static partial class Csrf
{
    public const string CookieName = "__Host-csrf";

    /// <summary>The form field. ZAP recognises this name as an anti-CSRF token.</summary>
    public const string Field = "csrf_token";

    /// <summary>A random value, not a credential: a login page left open for a week should still submit.</summary>
    public static readonly TimeSpan CookieMaxAge = TimeSpan.FromDays(30);

    private static readonly HashSet<string> UnsafeMethods = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    /// <summary><c>none</c> is a navigation the user started (typed, bookmarked); it carries no forged intent.</summary>
    private static readonly HashSet<string> TrustedSites = new(StringComparer.Ordinal) { "same-origin", "none" };

    private const string Minted = "csrf-new-token";

    /// <summary>What <see cref="Token"/> mints: 32 random bytes, URL-safe base64, 43 characters.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenShape();

    [GeneratedRegex("""<input\b[^>]*\bname=["']csrf_token["'][^>]*>""")]
    private static partial Regex FieldTag();

    [GeneratedRegex("""\bvalue=["']([^"']*)["']""")]
    private static partial Regex ValueAttribute();

    /// <summary>Whether a browser sent this request from another origin.</summary>
    public static bool CrossSite(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site))
        {
            return !TrustedSites.Contains(site.ToString().ToLowerInvariant());
        }

        if (!request.Headers.TryGetValue("Origin", out var originHeader))
        {
            return false;
        }

        var origin = originHeader.ToString();
        // "null" is an opaque origin: a sandboxed frame, a data: URL, a file.
        if (origin == "null")
        {
            return true;
        }

        var schemeEnd = origin.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? "" : origin[..schemeEnd];
        var netloc = schemeEnd < 0 ? "" : origin[(schemeEnd + 3)..].Split('/', 2)[0];
        return Host(netloc, scheme) != Host(request.Headers.Host.ToString(), scheme);
    }

    /// <summary><paramref name="netloc"/> lowercased, without the scheme's default port.</summary>
    private static string Host(string netloc, string scheme)
    {
        var lower = netloc.ToLowerInvariant();
        var port = scheme.ToLowerInvariant() switch
        {
            "http" => ":80",
            "https" => ":443",
            _ => null,
        };
        return port is not null && lower.EndsWith(port, StringComparison.Ordinal) ? lower[..^port.Length] : lower;
    }

    /// <summary>
    /// The form token for this browser: its cookie's, or a new one set on this
    /// response. A cookie that doesn't look like a token is replaced, so a junk
    /// value is never echoed into a page.
    /// </summary>
    public static string Token(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items[Minted] is string minted)
        {
            return minted;
        }

        var existing = context.Request.Cookies[CookieName] ?? "";
        if (TokenShape().IsMatch(existing))
        {
            return existing;
        }

        minted = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        context.Items[Minted] = minted;
        context.Response.Cookies.Append(CookieName, minted, new CookieOptions
        {
            MaxAge = CookieMaxAge,
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });
        return minted;
    }

    /// <summary>Whether the form's <c>csrf_token</c> matches this browser's cookie, compared in constant time.</summary>
    public static bool Valid(HttpRequest request, string presented)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(presented);
        var expected = request.Cookies[CookieName] ?? "";
        return expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>The <c>csrf_token</c> field's value in a rendered page, for tests that post forms.</summary>
    public static string TokenIn(string html)
    {
        var tag = FieldTag().Match(html);
        var value = tag.Success ? ValueAttribute().Match(tag.Value) : Match.Empty;
        return value.Success ? value.Groups[1].Value : throw new ArgumentException($"no {Field} field in the page", nameof(html));
    }

    /// <summary>Refuse cross-site unsafe requests, before anything else sees them.</summary>
    public static IApplicationBuilder UseCsrf(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (UnsafeMethods.Contains(context.Request.Method) && CrossSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Cross-site request refused").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    });
}

/// <summary>
/// Security headers on every response, the set career, finances and health
/// send. The passive DAST scan in jay-withers/security-testing reports each
/// one that is missing.
///
/// <b>Scripts are nonce-gated, styles are not.</b> Any inline script must
/// carry the response's nonce (<see cref="Nonce"/>); the pages have none that
/// execute (the chart payload is a <c>type="application/json"</c> block).
/// Styles keep <c>'unsafe-inline'</c> because the views carry many
/// <c>style=""</c> attributes. <b>HSTS is set here</b>: Container Apps ingress
/// adds none. <c>CSP_FORM_ACTION</c> adds origins a form POST may redirect to.
/// </summary>
public static partial class SecurityHeaders
{
    public static readonly IReadOnlyDictionary<string, string> Static = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains",
        ["X-Content-Type-Options"] = "nosniff",
        // Superseded by CSP frame-ancestors, but the only clickjacking defence an older browser understands.
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "strict-origin-when-cross-origin",
        ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
        // Everything is served from this one origin, so isolating it costs nothing.
        ["Cross-Origin-Opener-Policy"] = "same-origin",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["Cross-Origin-Embedder-Policy"] = "require-corp",
    };

    private const string NonceKey = "csp-nonce";

    public static string Nonce(HttpContext context) => context?.Items[NonceKey] as string ?? "";

    public static string ContentSecurityPolicy(string nonce, IEnumerable<string> formAction) => string.Join("; ",
        "default-src 'self'",
        $"script-src 'self' 'nonce-{nonce}'",
        "style-src 'self' 'unsafe-inline'",
        "object-src 'none'",
        "base-uri 'none'",
        string.Join(' ', formAction.Prepend("form-action 'self'")),
        "frame-ancestors 'none'");

    /// <summary>
    /// Outermost, so the headers land on everything, refusals and static files
    /// included, and an unhandled error becomes a plain 500 that carries them too.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IEnumerable<string> formAction)
    {
        var extra = formAction.ToList();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SecurityHeaders));
        return app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            context.Items[NonceKey] = nonce;
            context.Response.OnStarting(() =>
            {
                foreach (var (name, value) in Static)
                {
                    context.Response.Headers[name] = value;
                }

                context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(nonce, extra);
                return Task.CompletedTask;
            });
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception exc) when (!context.Response.HasStarted)
            {
                LogUnhandled(logger, exc, context.Request.Method, context.Request.Path);
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Internal Server Error").ConfigureAwait(false);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "unhandled error on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exc, string method, string path);
}
