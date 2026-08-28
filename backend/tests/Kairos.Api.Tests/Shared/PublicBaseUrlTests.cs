using Kairos.Api.Integrations.Email;
using Kairos.Api.Shared;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Kairos.Api.Tests.Shared;

/// <summary>
/// One public base URL, two consumers, and the precedence between them. Mail asked for this value
/// first and kept its own setting; the admin surface asked second and is why the canonical one is
/// not called Email.
/// </summary>
public class PublicBaseUrlTests
{
    private const string AppUrl = "https://kairos.example.test";

    /// <summary>A different host from <see cref="AppUrl"/>, because a test where the two agree
    /// cannot tell precedence from coincidence.</summary>
    private const string MailUrl = "https://mail.example.test";

    private const string AppPath = $"{AppOptions.SectionName}:{nameof(AppOptions.PublicBaseUrl)}";
    private const string MailPath = $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)}";

    [Fact]
    public void EmailPublicBaseUrl_WhenOnlyTheAppSettingIsSet_FallsBackToIt()
    {
        Resolve(appUrl: AppUrl, mailUrl: null).PublicBaseUrl.ShouldBe(AppUrl);
    }

    [Fact]
    public void EmailPublicBaseUrl_WhenBothAreSet_KeepsItsOwn()
    {
        Resolve(appUrl: AppUrl, mailUrl: MailUrl).PublicBaseUrl.ShouldBe(MailUrl);
    }

    [Fact]
    public void EmailPublicBaseUrl_WhenNeitherIsSet_StaysNull()
    {
        Resolve(appUrl: null, mailUrl: null).PublicBaseUrl.ShouldBeNull();
    }

    /// <summary>
    /// A blank is not a value. Mail configured with whitespace falls back rather than sending links
    /// to an empty origin — the same treatment an absent setting gets, because they mean the same
    /// thing to a reader and should mean the same thing here.
    /// </summary>
    [Fact]
    public void EmailPublicBaseUrl_WhenMailIsBlank_FallsBackToTheAppSetting()
    {
        Resolve(appUrl: AppUrl, mailUrl: "   ").PublicBaseUrl.ShouldBe(AppUrl);
    }

    /// <summary>
    /// Builds the options graph <c>Program.cs</c> builds, so what is asserted is the shipping
    /// registration rather than a second one written to agree with it.
    /// </summary>
    private static EmailOptions Resolve(string? appUrl, string? mailUrl)
    {
        var settings = new Dictionary<string, string?>();
        if (appUrl is not null)
        {
            settings[AppPath] = appUrl;
        }

        if (mailUrl is not null)
        {
            settings[MailPath] = mailUrl;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection().AddPublicBaseUrl(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<EmailOptions>>().Value;
    }
}
