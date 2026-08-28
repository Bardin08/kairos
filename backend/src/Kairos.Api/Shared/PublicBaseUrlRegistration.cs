using Kairos.Api.Integrations.Email;

using Microsoft.Extensions.Options;

namespace Kairos.Api.Shared;

/// <summary>
/// Binds the two option types that carry a public base URL, and makes one fall back to the other.
/// </summary>
public static class PublicBaseUrlRegistration
{
    /// <summary>
    /// Binds <see cref="AppOptions"/> and <see cref="EmailOptions"/>, then makes
    /// <see cref="EmailOptions.PublicBaseUrl"/> fall back to <see cref="AppOptions.PublicBaseUrl"/>
    /// when mail names none of its own.
    /// </summary>
    /// <remarks>
    /// An extension rather than two lines in <c>Program.cs</c>, so the fallback can be tested
    /// against the graph that actually ships instead of against a second one written for the test.
    /// <para>
    /// Post-configuration rather than a default on the property: the fallback has to run after
    /// binding, and a property initialiser cannot see configuration. Mail keeps winning wherever it
    /// is set, so no deployment changes behaviour on upgrade — that setting predates this one and
    /// is in use.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPublicBaseUrl(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<EmailOptions>, EmailPublicBaseUrlFallback>();

        return services;
    }
}

/// <summary>Applies <see cref="AppOptions.PublicBaseUrl"/> to mail that names none of its own.</summary>
internal sealed class EmailPublicBaseUrlFallback(IOptions<AppOptions> app)
    : IPostConfigureOptions<EmailOptions>
{
    public void PostConfigure(string? name, EmailOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            options.PublicBaseUrl = app.Value.PublicBaseUrl;
        }
    }
}
