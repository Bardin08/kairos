using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Kairos.Api.Security;

/// <summary>
/// Seals each data-protection key under <c>Security:SecretKey</c> before it reaches the volume, so a
/// copy of the volume or of a backup is not enough to forge an operator's session cookie. The key
/// every instance already refuses to start without, so encryption at rest costs a deployment nothing
/// new to configure or keep.
/// </summary>
public sealed class KeyRingEncryptor(SecretProtector protector) : IXmlEncryptor
{
    private const string ElementName = "sealedKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement) =>
        new(
            new XElement(
                ElementName,
                protector.Protect(plaintextElement.ToString(SaveOptions.DisableFormatting))),
            typeof(KeyRingDecryptor));
}
