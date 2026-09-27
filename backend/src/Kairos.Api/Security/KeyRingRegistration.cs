using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Kairos.Api.Security;

/// <summary>
/// The data-protection key ring that signs the operator's session cookie.
/// </summary>
public static class KeyRingRegistration
{
    private const string ApplicationName = "Kairos";

    /// <summary>
    /// Seals each key under <see cref="SecretProtector"/> and leaves the ring's location to the
    /// framework, which keeps it under $HOME/.aspnet — the image points HOME at the data volume, so
    /// the ring survives a redeploy and goes into the backup with the database.
    /// </summary>
    /// <remarks>
    /// An extension rather than lines in <c>Program.cs</c>, so a test can check the graph that
    /// ships without the harness's own ring directory on top of it.
    /// <para>
    /// The name is pinned because the default is the content root path, and a ring that outlives
    /// the process must not stop opening its own cookies when that path changes.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddKeyRing(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddOptions<KeyManagementOptions>()
            .Configure<SecretProtector>(
                (options, protector) => options.XmlEncryptor = new KeyRingEncryptor(protector));

        return services;
    }
}
