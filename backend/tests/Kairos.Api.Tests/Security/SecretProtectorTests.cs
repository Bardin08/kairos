using Kairos.Api.Security;

using Shouldly;

namespace Kairos.Api.Tests.Security;

public class SecretProtectorTests
{
    private const string Plaintext = "1//0eXampleRefreshTokenValue";

    private static SecretProtector Protector() =>
        new(Convert.ToBase64String(new byte[SecretProtector.KeyBytes]));

    [Fact]
    public void Unprotect_AfterProtect_ReturnsTheOriginal() =>
        Protector().Unprotect(Protector().Protect(Plaintext)).ShouldBe(Plaintext);

    [Fact]
    public void Protect_CalledTwiceOnTheSameInput_ProducesDifferentCiphertext()
    {
        // A repeated nonce in AES-GCM is catastrophic, not cosmetic: it leaks the
        // XOR of the plaintexts and destroys authentication. This is the test that
        // fails if someone "simplifies" the nonce to a constant.
        var protector = Protector();

        protector.Protect(Plaintext).ShouldNotBe(protector.Protect(Plaintext));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var protector = Protector();
        var bytes = Convert.FromBase64String(protector.Protect(Plaintext));
        bytes[^1] ^= 0xFF;

        Should.Throw<Exception>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
    }
}
