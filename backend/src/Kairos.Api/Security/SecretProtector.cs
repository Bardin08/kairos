using System.Security.Cryptography;
using System.Text;

namespace Kairos.Api.Security;

/// <summary>
/// AES-GCM for the handful of values that must not sit in the database in the clear.
/// The key arrives from configuration, sourced from 1Password by External Secrets Operator;
/// it is never generated at runtime, because a key the app invents cannot survive a restart.
/// </summary>
/// <remarks>
/// The reason this exists rather than a plain column: Litestream 0.5.x cannot encrypt and
/// silently ignores an age: block, so a refresh token written in the clear would be streamed
/// to object storage in the clear by a tool that accepted an encryption directive and dropped
/// it. Encrypting in the column makes the backup path's behaviour stop being load-bearing.
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
