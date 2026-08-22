using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.FileProviders;

namespace Kairos.Api.Security;

/// <summary>
/// What the single page is allowed to load and run, written once at startup from the page that is
/// actually on disk. Everything Kairos serves is same-origin — the fonts are in
/// <c>/fonts</c>, the stylesheet is vendored beside them, and the SPA fetches nothing but this
/// application's own API — so every directive is <c>'self'</c> or narrower, and the one thing that
/// needs naming is the inline script.
/// </summary>
/// <remarks>
/// The hash is computed here rather than baked in as a constant, because a constant is right the
/// day it is written and wrong the first time somebody edits that script — and it does not fail
/// loudly. The browser simply refuses the script, the pre-paint theme read never runs, and the
/// page flashes the wrong theme on every load with nothing in any log. Read once at startup and
/// not per request: the answer is the same for every response, and the page it is read from is
/// written by a build rather than by anything this process does.
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
    /// A <c>&lt;script&gt;</c> that carries no <c>src</c>, and the text it executes. The word
    /// boundary is what keeps <c>src</c> from matching inside a longer attribute name.
    /// </summary>
    [GeneratedRegex(
        @"<script\b(?![^>]*\bsrc\b)[^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScripts();
}
