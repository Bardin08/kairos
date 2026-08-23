using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.FileProviders;

namespace Kairos.Api.Security;

/// <summary>
/// What the single page is allowed to load and run, written once at startup from the page that is
/// actually on disk. Everything Kairos serves is same-origin — the fonts are in <c>/fonts</c>, the
/// stylesheet is vendored beside them, and the SPA fetches nothing but this application's own API —
/// so every directive is <c>'self'</c> or narrower, and only the inline script needs naming.
/// </summary>
/// <remarks>
/// The hash is computed here rather than baked in as a constant, because a constant is right the
/// day it is written and wrong the first time somebody edits that script — and it fails quietly:
/// the browser refuses the script, the pre-paint theme read never runs, and the page flashes the
/// wrong theme on every load with nothing in any log. Read once at startup rather than per request,
/// because the page it reads is written by a build rather than by anything this process does.
/// </remarks>
/// <remarks>
/// No <c>report-to</c> and no <c>report-uri</c>, decided rather than overlooked. A reporting
/// endpoint is an unauthenticated write route accepting JSON from any browser that has ever loaded
/// this origin, and Kairos would have to rate-limit it, bound it and give an operator somewhere to
/// read it — a task, not a directive. Of the two silent failures it would cover, the hash drifting
/// from the page is closed above, and a directive that turns out too tight shows up in a console
/// the first time the page is opened. Reporting, if wanted later, arrives with its own route and
/// its own retention, not as a line in this string.
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
    /// A <c>&lt;script&gt;</c> that carries no <c>src</c>, and the text it executes. The lookahead
    /// matches an attribute NAMED src — whitespace, the name, then its equals sign — not the
    /// letters wherever they appear. A word boundary is not enough: it excludes <c>srcset</c>, but a
    /// hyphen is a non-word character, so <c>data-src</c> satisfies it too and the script is
    /// skipped. Skipping fails silently — the policy stays well formed, the browser refuses a script
    /// nobody hashed, and nothing is logged.
    /// </summary>
    [GeneratedRegex(
        @"<script\b(?![^>]*\ssrc\s*=)[^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScripts();
}
