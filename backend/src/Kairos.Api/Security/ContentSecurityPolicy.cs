using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.FileProviders;

namespace Kairos.Api.Security;

/// <summary>
/// What the single page is allowed to load and run, written once at startup from the page on disk.
/// Everything Kairos serves is same-origin — vendored fonts and stylesheet, no fetch but its own
/// API — so every directive is <c>'self'</c> or narrower and only the inline script needs naming.
/// </summary>
/// <remarks>
/// The hash is computed rather than baked in as a constant: a constant goes wrong the first time
/// somebody edits that script, and fails quietly — the browser refuses it, the pre-paint theme read
/// never runs, and the page flashes the wrong theme with nothing in any log. Once at startup, not
/// per request, because a build writes that page and this process does not.
/// </remarks>
/// <remarks>
/// No <c>report-to</c> and no <c>report-uri</c>, decided rather than overlooked. A reporting
/// endpoint is an unauthenticated write route taking JSON from any browser that ever loaded this
/// origin, to be rate-limited, bounded and read by somebody — a task, not a directive. Of the two
/// silent failures it would cover, the hash drifting is closed above, and a too-tight directive
/// shows up in a console the first time the page opens. It would arrive with its own route and
/// retention, not as a line in this string.
/// </remarks>
public static partial class ContentSecurityPolicy
{
    /// <summary>The page the policy is derived from, and the one the fallback route serves.</summary>
    public const string IndexPage = "index.html";

    private const string Self = "'self'";

    private const string Nothing = "'none'";

    private const string Separator = "; ";

    /// <summary>
    /// The policy for the page in this web root. A web root with no <see cref="IndexPage"/> in it
    /// is a backend-only host — the single page is built by another job and is simply not there —
    /// and a page that does not exist has no inline script to allow.
    /// </summary>
    public static string For(IFileProvider files) => Policy(InlineScriptHashes(Read(files)));

    private static string Policy(IEnumerable<string> scriptHashes) => string.Join(
        Separator,
        $"default-src {Nothing}",
        $"script-src {string.Join(' ', [Self, .. scriptHashes])}",
        $"style-src {Self}",
        $"font-src {Self}",
        $"img-src {Self}",
        $"connect-src {Self}",
        $"form-action {Self}",
        $"frame-ancestors {Nothing}",
        $"base-uri {Nothing}",
        $"object-src {Nothing}");

    /// <summary>
    /// Exactly the bytes between the tags, with nothing trimmed or normalised: that substring is
    /// what a browser hashes, so a tidier one produces a valid-looking hash that matches nothing.
    /// </summary>
    private static IEnumerable<string> InlineScriptHashes(string page) => InlineScripts()
        .Matches(page)
        .Select(match => Hash(match.Groups[1].Value));

    private static string Hash(string script) =>
        $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    private static string Read(IFileProvider files)
    {
        var page = files.GetFileInfo(IndexPage);
        if (!page.Exists)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(page.CreateReadStream());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A <c>&lt;script&gt;</c> carrying no <c>src</c>, and the text it executes. The lookahead
    /// matches an attribute NAMED src — whitespace, name, equals — not the letters wherever they
    /// appear. A word boundary excludes <c>srcset</c> but not <c>data-src</c>, whose hyphen is a
    /// non-word character, and the script would be skipped silently: policy still well formed,
    /// browser refusing a script nobody hashed, nothing logged.
    /// </summary>
    [GeneratedRegex(
        @"<script\b(?![^>]*\ssrc\s*=)[^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScripts();
}
