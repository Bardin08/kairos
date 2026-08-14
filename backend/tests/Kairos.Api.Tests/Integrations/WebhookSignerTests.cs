using Kairos.Api.Integrations.Webhooks;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

public class WebhookSignerTests
{
    private const string Secret = "a-shared-secret";
    private const string Body = """{"type":"meeting.ended"}""";
    private const long Timestamp = 1_788_000_000;

    private static WebhookSigner Signer() => new(Secret);

    [Fact]
    public void Sign_SameInputs_IsDeterministic() =>
        Signer().Sign(Timestamp, Body).ShouldBe(Signer().Sign(Timestamp, Body));

    [Fact]
    public void Sign_DifferentTimestamp_ProducesADifferentSignature() =>
        // The timestamp is inside the signed payload, not merely alongside it. Otherwise a
        // captured delivery can be replayed forever with its signature still valid.
        Signer().Sign(Timestamp, Body).ShouldNotBe(Signer().Sign(Timestamp + 1, Body));

    [Fact]
    public void Sign_DifferentSecret_ProducesADifferentSignature() =>
        new WebhookSigner("other").Sign(Timestamp, Body).ShouldNotBe(Signer().Sign(Timestamp, Body));
}
