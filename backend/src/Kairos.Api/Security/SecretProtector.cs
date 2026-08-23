using System.Security.Cryptography;
using System.Text;

namespace Kairos.Api.Security;

/// <summary>
/// AES-GCM for the handful of values that must not sit in the database in the clear. The key comes
/// from configuration, via 1Password and External Secrets Operator, never generated at runtime — an
/// invented key cannot survive a restart.
/// </summary>
/// <remarks>
/// Why a column and not the backup: Litestream 0.5.x cannot encrypt and silently ignores an age:
/// block, so a clear refresh token would be streamed to object storage in the clear by a tool that
/// accepted the directive and dropped it. Encrypting here stops the backup path being load-bearing.
/// </remarks>
public sealed class SecretProtector
{
    public const int KeyBytes = 32;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public SecretProtector(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != KeyBytes)
        {
            throw new ArgumentException($"Key must be {KeyBytes} bytes.", nameof(base64Key));
        }
    }

    /// <summary>Layout is nonce || ciphertext || tag, base64. Self-describing, no side table.</summary>
    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(NonceBytes, plain.Length),
            output.AsSpan(NonceBytes + plain.Length, TagBytes));

        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedValue)
    {
        var input = Convert.FromBase64String(protectedValue);
        var cipherLength = input.Length - NonceBytes - TagBytes;
        var plain = new byte[cipherLength];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(
            input.AsSpan(0, NonceBytes),
            input.AsSpan(NonceBytes, cipherLength),
            input.AsSpan(NonceBytes + cipherLength, TagBytes),
            plain);

        return Encoding.UTF8.GetString(plain);
    }
}
