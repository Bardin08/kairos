using Google.Apis.Http;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// Google's factory, handing back <see cref="FakeGoogleHandler"/>. Its <c>CreateHandler</c> is the
/// one hook the client library offers for this.
/// </summary>
public sealed class FakeGoogleHttpFactory(FakeGoogleHandler handler) : HttpClientFactory
{
    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
}
