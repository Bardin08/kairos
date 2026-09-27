using System.Net;
using System.Security.Claims;
using System.Xml.Linq;

using Kairos.Api.Persistence;
using Kairos.Api.Security;
using Kairos.Api.Shared;
using Kairos.Api.Tests.Features;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Kairos.Api.Tests.Security;

/// <summary>
/// The operator's session cookie against a key ring that outlives the process: a second host over
/// the first one's ring is what a redeploy over the same volume looks like from inside.
/// </summary>
public sealed class KeyRingTests : IDisposable
{
    private const string AlertsPath = $"{ApiVersions.V1}/admin/alerts";

    private const string KeyFilePattern = "key-*.xml";

    /// <summary>
    /// The element the framework writes in place of the plain master key once an encryptor has
    /// sealed it. Matched by local name, since it sits in the framework's own XML namespace.
    /// </summary>
    private const string EncryptedSecretElement = "encryptedSecret";

    private readonly string _ring = Path.Combine(
        Path.GetTempPath(), $"kairos-ring-{Guid.CreateVersion7():N}");

    private readonly string _secretKey = KairosApp.NewSecretKey();

    [Fact]
    public async Task Cookie_IssuedBeforeARedeploy_OpensTheAdminAfterIt()
    {
        var cookie = CookieFromAHostThatIsGone(_secretKey);

        using var after = new KairosApp(keyRingDirectory: _ring, secretKey: _secretKey);
        var response = await SendWithCookie(after, cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The discriminator defaults to the content root, so an image that moved its application
    /// directory would sign the operator out on the release that moved it.
    /// </summary>
    [Fact]
    public async Task Cookie_AfterTheContentRootMoves_StillOpensTheAdmin()
    {
        var cookie = CookieFromAHostThatIsGone(_secretKey);
        var movedRoot = Directory.CreateTempSubdirectory("kairos-root-").FullName;

        try
        {
            using var after = new KairosApp(keyRingDirectory: _ring, secretKey: _secretKey);
            using var moved = after.WithWebHostBuilder(builder => builder.UseContentRoot(movedRoot));
            var response = await SendWithCookie(moved, cookie);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(movedRoot, recursive: true);
        }
    }

    [Fact]
    public void KeyRing_OnFirstUse_IsWrittenEncrypted()
    {
        CookieFromAHostThatIsGone(_secretKey);

        var key = Directory.GetFiles(_ring, KeyFilePattern).ShouldHaveSingleItem();

        XDocument.Load(key).Descendants()
            .ShouldContain(element => element.Name.LocalName == EncryptedSecretElement);
    }

    /// <summary>
    /// The registration that ships, without the harness's ring directory on top of it. The image
    /// decides where the ring lives by pointing HOME at the volume; a repository chosen in code
    /// would quietly take that decision back.
    /// </summary>
    [Fact]
    public void AddKeyRing_OnItsOwn_SealsKeysWithoutChoosingWhereTheyLive()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new SecretProtector(_secretKey))
            .AddKeyRing()
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.XmlEncryptor.ShouldBeOfType<KeyRingEncryptor>();
        options.XmlRepository.ShouldBeNull();
    }

    /// <summary>
    /// A rotated secret key is a sign-in, not an outage: the old key no longer opens, the ring
    /// mints a new one, and the operator is asked who they are.
    /// </summary>
    [Fact]
    public async Task Cookie_AfterTheSecretKeyChanges_IsRefusedNotAFault()
    {
        var cookie = CookieFromAHostThatIsGone(_secretKey);

        using var rotated = new KairosApp(keyRingDirectory: _ring, secretKey: KairosApp.NewSecretKey());
        var response = await SendWithCookie(rotated, cookie);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        if (Directory.Exists(_ring))
        {
            Directory.Delete(_ring, recursive: true);
        }
    }

    /// <summary>
    /// Sealed by the application's own cookie handler, so the purpose strings are the ones a real
    /// sign-in uses, and carrying what the admin policy asks of an operator.
    /// </summary>
    private string CookieFromAHostThatIsGone(string secretKey)
    {
        using var before = new KairosApp(keyRingDirectory: _ring, secretKey: secretKey);

        var options = before.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(
            [
                new Claim(AdminAuthorization.GroupClaimType, AdminAuthorization.DefaultGroup),
                new Claim(ClaimTypes.NameIdentifier, OperatorSession.Subject),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);

        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    private static async Task<HttpResponseMessage> SendWithCookie(
        WebApplicationFactory<KairosDbContext> app, string cookie)
    {
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AlertsPath);
        request.Headers.Add("Cookie", cookie);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
