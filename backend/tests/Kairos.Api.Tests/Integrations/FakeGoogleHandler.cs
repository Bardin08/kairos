using System.IO.Compression;
using System.Net;
using System.Text;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// Google, as far as the adapter can tell. Canned responses keyed by the path being asked for, and
/// a record of every request that went out — which is what these tests are really about: the defect
/// this seam exposes is not what Kairos did with the answer, it is what it asked.
/// </summary>
/// <remarks>
/// An <see cref="HttpMessageHandler"/> under Google's own factory rather than an interface wrapping
/// the client library. Wrapping it would leave the request-building code — the part that is wrong —
/// on the untested side of the seam.
/// </remarks>
public sealed class FakeGoogleHandler : HttpMessageHandler
{
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// What the client library compresses a request body with when <c>GZipEnabled</c> is left at
    /// its default, which is on. A recorded body has to be the JSON that was sent, not the bytes.
    /// </summary>
    private const string GzipEncoding = "gzip";

    /// <summary>Google's OAuth token endpoint, which the credential refresh goes through.</summary>
    private const string TokenPath = "/token";

    /// <summary>
    /// An access token that has not expired, so <c>UserCredential</c> stops asking. The value is
    /// never inspected — only that one came back.
    /// </summary>
    private static readonly string AccessTokenGrant =
        $$"""{"access_token":"not-a-real-access-token","token_type":"Bearer","expires_in":{{AccessTokenLifetimeSeconds}}}""";

    /// <summary>What Google's token endpoint answers a refresh token it no longer honours.</summary>
    private const string RefusedGrant =
        """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""";

    /// <summary>
    /// The access token's lifetime in <see cref="AccessTokenGrant"/>, public so a test that moves the
    /// clock can move it past exactly this.
    /// </summary>
    public const int AccessTokenLifetimeSeconds = 3600;

    private readonly Dictionary<string, CannedResponse> _responses = [];

    private bool _grantRefused;

    /// <summary>Token requests counted apart from <see cref="Requests"/>, which leaves them out.</summary>
    public int TokenRequests { get; private set; }

    /// <summary>
    /// Every request, in order, with its body already read. <c>Uri</c> is absolute and
    /// <c>Path</c> is the path alone, so an assertion can pick whichever it means.
    /// </summary>
    public List<(string Uri, string Path, string Body)> Requests { get; } = [];

    /// <summary>
    /// Every request that matched no stub. The throw below only reaches a test whose caller does
    /// not swallow it, and the read path swallows by design — <c>GetBusyAsync</c> catches and
    /// returns nothing busy, <c>DescribeSettingsAsync</c> answers with null options. Asserting
    /// this is empty is therefore what makes a forgotten stub fail rather than pass quietly.
    /// </summary>
    public List<string> UnstubbedUris { get; } = [];

    /// <summary>
    /// <paramref name="pathFragment"/> is matched by <c>Contains</c> on the absolute URI, longest
    /// fragment first, so one stub can answer a family of paths and a second override it for one
    /// member of that family.
    /// </summary>
    /// <param name="status">
    /// Returned rather than thrown, including for a 5xx: an exception out of here is a transport
    /// fault the client library retries differently, and the point of a canned 503 is to put the
    /// library's own retry loop in play rather than to skip it.
    /// </param>
    public FakeGoogleHandler Answering(
        string pathFragment,
        string json,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[pathFragment] = new CannedResponse(status, json);
        return this;
    }

    public FakeGoogleHandler RefusingGrant()
    {
        _grantRefused = true;
        return this;
    }

    public FakeGoogleHandler RestoringGrant()
    {
        _grantRefused = false;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        var body = await ReadBodyAsync(request.Content, cancellationToken);

        // The token refresh is plumbing, not a request any test asserts on, and counting it would
        // make every "asked Google once" assertion wrong. Answered before the recording.
        if (uri.Contains(TokenPath, StringComparison.Ordinal))
        {
            TokenRequests++;
            return _grantRefused
                ? Respond(HttpStatusCode.BadRequest, RefusedGrant)
                : Respond(HttpStatusCode.OK, AccessTokenGrant);
        }

        Requests.Add((uri, path, body));

        // Longest fragment wins, ties broken on the fragment itself. Taking the first dictionary
        // entry that matched made the answer depend on enumeration order: registering "/events"
        // and "/calendars/" against a calendars/primary/events URI answered from whichever was
        // added first. Longest rather than refusing the overlap, because the overlap is useful —
        // it is how a suite stubs a family of paths and then overrides one member of it.
        var match = _responses
            .Where(candidate => uri.Contains(candidate.Key, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Key.Length)
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .Select(candidate => candidate.Value)
            .FirstOrDefault();

        // An unstubbed path is a test that has drifted from the code. Thrown rather than answered
        // with a 404, which would read as Google saying no and be diagnosed as the adapter's
        // fault. Recorded first because the throw is not reliably visible: a caller that swallows
        // provider failures — which the read path does, deliberately — turns it back into an
        // ordinary empty answer, and only UnstubbedUris survives that.
        if (match is null)
        {
            UnstubbedUris.Add(uri);
            throw new InvalidOperationException($"No canned Google response for {uri}.");
        }

        return Respond(match.Status, match.Json);
    }

    /// <remarks>
    /// Buffered rather than read off the content's stream. The client library re-sends the same
    /// <see cref="HttpRequestMessage"/> on a retry, and consuming — worse, disposing — the request
    /// stream left the second attempt reading a stream that no longer supported reading. What that
    /// cost was not a bad message but a silent one: the retry died in here, so a test aimed at the
    /// library's retry behaviour saw a single attempt and an exception from the harness.
    /// </remarks>
    private static async Task<string> ReadBodyAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var bytes = await content.ReadAsByteArrayAsync(cancellationToken);

        if (!content.Headers.ContentEncoding.Contains(GzipEncoding))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        using var raw = new MemoryStream(bytes);
        await using var plain = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(plain, Encoding.UTF8);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
        };

    /// <summary>Private and nested because it never leaves this class.</summary>
    private sealed record CannedResponse(HttpStatusCode Status, string Json);
}
