using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Kairos.Api.Security;

/// <summary>
/// The other half of <see cref="KeyRingEncryptor"/>. The framework builds it by name from the type
/// recorded in each key file, handing it only the service provider, so the protector is resolved
/// here rather than injected.
/// </summary>
public sealed class KeyRingDecryptor(IServiceProvider services) : IXmlDecryptor
{
    private readonly SecretProtector _protector = services.GetRequiredService<SecretProtector>();

    public XElement Decrypt(XElement encryptedElement) =>
        XElement.Parse(_protector.Unprotect(encryptedElement.Value));
}
