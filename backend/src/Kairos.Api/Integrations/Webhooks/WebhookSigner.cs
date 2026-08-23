using System.Security.Cryptography;
using System.Text;

namespace Kairos.Api.Integrations.Webhooks;

/// <summary>
/// Proves a delivery came from this Kairos and not from whoever else found the endpoint URL.
/// HMAC-SHA256 over the raw body, keyed by a secret only the two ends know.
/// </summary>
/// <remarks>
/// The timestamp is signed <em>with</em> the body, not merely sent alongside it. Signing the body
/// alone leaves a captured delivery valid forever; inside the signed string the receiver can
/// reject anything older than its window, and the signature stops the timestamp being edited.
/// </remarks>
public sealed class WebhookSigner
{
    /// <summary>Carries <c>sha256=&lt;hex&gt;</c> — exactly what <see cref="Sign"/> returns.</summary>
    public const string SignatureHeader = "X-Kairos-Signature";

    /// <summary>Unix seconds, and the same value that went into the signature.</summary>
    public const string TimestampHeader = "X-Kairos-Timestamp";

    /// <summary>Named so a second algorithm is a second prefix rather than a breaking change.</summary>
    private const string Algorithm = "sha256";

    private readonly byte[] _key;

    public WebhookSigner(string secret) => _key = Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// The value for <see cref="SignatureHeader"/>. The build sets InvariantGlobalization, so the
    /// timestamp renders the same everywhere.
    /// </summary>
    public string Sign(long timestamp, string body)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        return $"{Algorithm}={Convert.ToHexStringLower(HMACSHA256.HashData(_key, signed))}";
    }
}
