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
    private const string AccessTokenGrant =
        """{"access_token":"not-a-real-access-token","token_type":"Bearer","expires_in":3600}""";

    private readonly Dictionary<string, string> _responses = [];

    /// <summary>Every request, in order, with its body already read.</summary>
    public List<(string Path, string Body)> Requests { get; } = [];

    /// <summary><paramref name="pathFragment"/> is matched by <c>Contains</c> on the absolute URI.</summary>
    public FakeGoogleHandler Answering(string pathFragment, string json)
    {
        _responses[pathFragment] = json;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

        var body = await ReadBodyAsync(request.Content, cancellationToken);

        // The token refresh is plumbing, not a request any test asserts on, and counting it would
        // make every "asked Google once" assertion wrong. Answered before the recording.
        if (uri.Contains(TokenPath, StringComparison.Ordinal))
        {
            return Json(AccessTokenGrant);
        }

        Requests.Add((uri, body));

        var match = _responses.FirstOrDefault(
            candidate => uri.Contains(candidate.Key, StringComparison.Ordinal));

        // An unstubbed path is a test that has drifted from the code. Thrown rather than answered
        // with a 404, which would read as Google saying no and be diagnosed as the adapter's fault.
        if (match.Key is null)
        {
            throw new InvalidOperationException($"No canned Google response for {uri}.");
        }

        return Json(match.Value);
    }

    private static async Task<string> ReadBodyAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (!content.Headers.ContentEncoding.Contains(GzipEncoding))
        {
            return await content.ReadAsStringAsync(cancellationToken);
        }

        await using var compressed = await content.ReadAsStreamAsync(cancellationToken);
        await using var plain = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(plain, Encoding.UTF8);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, JsonMediaType),
        };
}
