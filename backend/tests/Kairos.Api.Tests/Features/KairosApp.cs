using System.Globalization;
using System.Security.Cryptography;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Persistence;
using Kairos.Api.Security;
using Kairos.Api.Shared;
using Kairos.Api.Tests.Integrations;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

// Aliased because Google's factory interface collides by name with the one in System.Net.Http.
using GoogleHttpFactory = Google.Apis.Http.IHttpClientFactory;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The application as it ships, over a throwaway SQLite file. These tests are about the wiring —
/// routing, binding, DataAnnotations, the exception handler, the composition root — so what is
/// replaced is replaced for a reason written down beside it, and nothing else is.
/// </summary>
/// <remarks>
/// Three substitutions, and two of them are unconditional — every test gets them whether it asked
/// or not. <c>DbContextOptions&lt;KairosDbContext&gt;</c> is rebuilt by hand, and the hosted
/// <see cref="FollowUpWorker"/> is removed and re-registered as a plain singleton; both reasons are
/// written down in <see cref="ConfigureWebHost"/>. The third is optional and is
/// <paramref name="google"/>.
///
/// Typed on <see cref="KairosDbContext"/> rather than Program: the factory needs any type from
/// the application's assembly, and reusing one saves Program.cs a public partial class.
/// </remarks>
/// <param name="webRoot">
/// A directory to serve static files from, for a test needing an <c>index.html</c> it chose. Left
/// null it is the application's own, which holds a built page only where the frontend CI job ran.
/// That is also why a test expecting a path NOT to be served asserts more than the status code:
/// with a page present <c>MapFallbackToFile</c> answers 200 for any unmapped extensionless path.
/// </param>
/// <param name="google">
/// A stand-in for Google's HTTP, or null to leave the real one in place: an adapter test has to see
/// the request that went out, and a request that went out over the network went to somebody else's
/// server.
///
/// It does a second thing, and the second thing is observable. Passing a handler also sets the
/// client id, secret and redirect URI, which flips <see cref="GoogleClientFactory.IsConfigured"/>
/// and with it what the integrations surface reports. The two are welded together, so "configured
/// but never connected" — a real state of a fresh deployment — cannot be asked for here without
/// also faking the transport.
/// </param>
/// <param name="keyRingDirectory">
/// Where the data-protection keys go, for a test standing a second host on the first one's ring the
/// way a redeploy stands a new container on the old volume. A directory passed in belongs to the
/// caller and outlives this instance; left null, the instance makes one and deletes it.
/// </param>
/// <param name="secretKey">
/// The <c>Security:SecretKey</c> this deployment runs under, passed with
/// <paramref name="keyRingDirectory"/> when the second host is the same deployment. Left null, one is
/// minted per instance.
/// </param>
public sealed class KairosApp(
    string? webRoot = null,
    string? publicBaseUrl = null,
    FakeGoogleHandler? google = null,
    string? keyRingDirectory = null,
    string? secretKey = null)
    : WebApplicationFactory<KairosDbContext>
{
    /// <summary>
    /// The identity provider the harness names. It resolves to nothing on purpose: the one test
    /// that needs OIDC discovery hands the handler a configuration of its own.
    /// </summary>
    public const string Authority = "https://id.kairos.test";

    /// <summary>Far above anything one test does, and finite so a runaway loop still stops.</summary>
    private const int TestPermits = 100_000;

    /// <summary>
    /// Client credentials, so <see cref="GoogleClientFactory"/> reports itself configured. None of
    /// the three is ever sent anywhere, because the fake handler answers before the socket.
    /// </summary>
    private const string GoogleClientId = "not-a-real-client-id.apps.googleusercontent.com";

    private const string GoogleClientSecret = "not-a-real-client-secret";

    private const string GoogleRedirectUri = "https://kairos.test/integrations/google/callback";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kairos-api-{Guid.CreateVersion7():N}.db");

    private readonly bool _ownsKeyRing = keyRingDirectory is null;

    private HttpClient? _client;

    /// <summary>
    /// Once per instance rather than once per container: <c>WithWebHostBuilder</c> derives a
    /// container over the same key ring, and two secret keys on one ring is a broken deployment,
    /// not a test setup.
    /// </summary>
    public string SecretKey { get; } = secretKey ?? NewSecretKey();

    public string KeyRingDirectory { get; } = keyRingDirectory
        ?? Path.Combine(Path.GetTempPath(), $"kairos-keys-{Guid.CreateVersion7():N}");

    public static string NewSecretKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretProtector.KeyBytes));

    public HttpClient Client => _client ??= CreateClient();

    /// <summary>
    /// The same database, for a test needing a second connection to it — holding a write lock
    /// while a request runs is the only way to reach the contention path from outside.
    /// </summary>
    public string ConnectionString => $"Data Source={_path}";

    /// <summary>
    /// The file that connection string points at, for the one test that takes the database away
    /// from a running application. Exposed rather than re-derived, since a test deleting a path
    /// it computed itself would pass over a database nothing was using.
    /// </summary>
    public string DatabasePath => _path;

    /// <summary>Writes through the application's own context, so the schema is the migrated one.</summary>
    public void Seed(Action<KairosDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        seed(context);
        context.SaveChanges();
    }

    public TResult Read<TResult>(Func<KairosDbContext, TResult> read)
    {
        using var scope = Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<KairosDbContext>());
    }

    /// <summary>
    /// The connection string is overridden through configuration, as an operator would. The
    /// options are then rebuilt by hand: AddDbContext keys EF's model on the host's service
    /// provider, and <see cref="Kairos.Api.Availability.AvailabilityQuery"/>'s compiled queries
    /// are static and bind to the first model they meet, so a second one fails the later suite
    /// with "executed with a different model than it was compiled against". Built this way the
    /// model is <see cref="TestDatabase"/>'s. Same context, provider and connection string.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = $"Data Source={_path}";

        builder.UseSetting("ConnectionStrings:Db", connectionString);

        if (webRoot is not null)
        {
            builder.UseWebRoot(webRoot);
        }

        // The application refuses to start without a key, so the harness supplies one as an
        // operator does. Thrown away with the instance unless a test hands it to a second host.
        builder.UseSetting("Security:SecretKey", SecretKey);

        // Same rule for Pocket ID: no issuer and client, no startup. Almost every test stays on
        // the anonymous side of the gate, and the one that challenges pins the handler's
        // configuration itself, so this address is never fetched from.
        // Unset for almost every test, because almost nothing depends on it. The settings read
        // reports it, and a deployment that serves its operator and its invitees on two hostnames
        // is the reason it exists — so the tests that care name their own.
        if (publicBaseUrl is not null)
        {
            builder.UseSetting(
                $"{AppOptions.SectionName}:{nameof(AppOptions.PublicBaseUrl)}", publicBaseUrl);
        }

        builder.UseSetting("Authentication:Authority", Authority);
        builder.UseSetting("Authentication:ClientId", "kairos-tests");
        builder.UseSetting("Authentication:ClientSecret", "kairos-tests");

        // Every other suite makes far more than ten requests through one partition — the test
        // host has no remote address — and none of them is about throttling. The suite that IS
        // about throttling sets the shipping numbers back.
        builder.UseSetting(
            $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.WritePermits)}",
            TestPermits.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting(
            $"{RateLimitOptions.SectionName}:{nameof(RateLimitOptions.ReadPermits)}",
            TestPermits.ToString(CultureInfo.InvariantCulture));

        builder.ConfigureServices(services =>
        {
            // The application keeps its ring where HOME points, which on a developer's machine is
            // their real ~/.aspnet. Every run would leave keys there sealed under a secret key the
            // next run never sees, so each instance gets a ring of its own.
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(KeyRingDirectory));

            services.RemoveAll<DbContextOptions<KairosDbContext>>();
            services.AddSingleton(
                new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(connectionString).Options);

            // The real hosted FollowUpWorker races a test that drains it directly — two callers
            // against one row, which is the opposite of OutboxDispatcher's single-instance
            // assumption. Removed here rather than left running: a test that needs the queue
            // worked drains it itself, the documented house pattern, and one that does not gets a
            // queue that sits still instead of a background pass timed against the assertion.
            var hosted = services
                .Where(descriptor =>
                    (descriptor.ServiceType == typeof(IHostedService))
                    && (descriptor.ImplementationType == typeof(FollowUpWorker)))
                .ToList();
            foreach (var descriptor in hosted)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<FollowUpWorker>();
        });

        // A block rather than an early return, so what comes after it is unconditional by
        // default. The return scoped everything below to "a fake Google was passed" without
        // saying so, and nothing would have failed for the next thing appended here.
        if (google is not null)
        {
            builder.UseSetting(
                $"{GoogleOptions.SectionName}:{nameof(GoogleOptions.ClientId)}", GoogleClientId);
            builder.UseSetting(
                $"{GoogleOptions.SectionName}:{nameof(GoogleOptions.ClientSecret)}",
                GoogleClientSecret);
            builder.UseSetting(
                $"{GoogleOptions.SectionName}:{nameof(GoogleOptions.RedirectUri)}",
                GoogleRedirectUri);

            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<GoogleHttpFactory>(
                    new FakeGoogleHttpFactory(google))));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        _client?.Dispose();

        // The pool holds handles to the file, so clearing it first is what makes the delete
        // succeed. This pool only: ClearAllPools is process-global, and with collections running
        // in parallel it disposes siblings' connections mid-test.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));

        string[] files = [_path, $"{_path}-wal", $"{_path}-shm"];
        foreach (var file in files)
        {
            File.Delete(file);
        }

        if (_ownsKeyRing && Directory.Exists(KeyRingDirectory))
        {
            Directory.Delete(KeyRingDirectory, recursive: true);
        }
    }
}
