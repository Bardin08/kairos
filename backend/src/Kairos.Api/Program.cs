using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Integrations;
using Kairos.Api.Features.Links;
using Kairos.Api.Features.Slots;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Integrations.Google;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Scheduling;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NodaTime;

// CreateSlimBuilder rather than CreateBuilder: it leaves out the configuration providers and
// logging this app never reads, which is startup time and a few Mi of a Raspberry Pi's memory
// that a self-hosted single-organizer scheduler has better uses for.
var builder = WebApplication.CreateSlimBuilder(args);

// AddDbContext, not AddDbContextPool: KairosDbContext registers its pragma interceptor from
// OnConfiguring, and pooling refuses a context that configures itself — "'OnConfiguring' cannot
// be used to modify DbContextOptions when DbContext pooling is enabled", thrown at the first
// migration. The pragmas are not optional, so the pool is what gives way.
builder.Services.AddDbContext<KairosDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=kairos.db"));

builder.Services.AddSingleton<IClock>(SystemClock.Instance);

const string secretKeyPath = "Security:SecretKey";

// Constructed here rather than resolved lazily, so an absent, non-base64 or wrong-length key
// stops the process at startup. An app that boots without its key and finds out at the first
// OAuth callback has turned a configuration error into a runtime mystery weeks later.
builder.Services.AddSingleton(new SecretProtector(
    builder.Configuration[secretKeyPath] ?? throw new InvalidOperationException(
        $"{secretKeyPath} is not configured. Kairos stores provider credentials encrypted at "
        + $"rest and needs a base64 {SecretProtector.KeyBytes}-byte AES-GCM key. Generate one "
        + $"with: openssl rand -base64 {SecretProtector.KeyBytes}")));

// Stateless and sealed — one instance, on the hot path of every booking page.
builder.Services.AddSingleton<SlotGenerator>();

// Part A shipped no authentication and Pocket ID is Part C, so the operator routes under
// /integrations are guarded by a shared token or by nothing at all. Read here, with every other
// configuration key, and handed to the group rather than resolved somewhere deeper.
const string adminTokenPath = "Integrations:AdminToken";
var integrationAdminToken = builder.Configuration[adminTokenPath];

builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));

// Registered twice, resolved once: the endpoints see an IIntegrationConnector and never a Google
// type, while Part B's calendar work asks for the factory by name. One instance either way,
// because the in-memory access token is only a cache if there is a single holder of it.
builder.Services.AddSingleton<GoogleClientFactory>();
builder.Services.AddSingleton<IIntegrationConnector>(
    services => services.GetRequiredService<GoogleClientFactory>());

// Scoped: it reads and writes through the request's DbContext.
builder.Services.AddScoped<IntegrationCredentialStore>();

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));

// All three are stateless and hold no per-request state: the builder renders text, the sender
// opens a connection per message, and the mailer only composes. Singletons, like SlotGenerator.
builder.Services.AddSingleton<IcsBuilder>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<BookingMailer>();

// Scoped, not singleton: they read the request's DbContext. SlotQuery takes
// IEnumerable<IBusySource>, so the calendar source is one more line here and no change anywhere
// else — and a third provider would be a third line and still nothing in Scheduling/.
// Registered unconditionally: unconfigured and never-connected are answered inside the adapter,
// which returns no busy time rather than throwing, so a Kairos with no Google is a working Kairos.
builder.Services.AddScoped<IBusySource, LocalBusySource>();
builder.Services.AddScoped<AvailabilityQuery>();
builder.Services.AddScoped<SlotQuery>();
builder.Services.AddScoped<BookingWriter>();

// One adapter resolved through three interfaces, and one instance per request rather than three:
// registering the class separately and handing the same instance to each role is what keeps a
// single Google client per scope. Busy sources are a collection because every source has a say;
// the writer and the conference creator are single because a meeting goes into one calendar. A
// second provider is therefore a second IBusySource line beside this one and a replacement of the
// other two — the shape of the decision, not an oversight.
builder.Services.AddScoped<GoogleCalendarAdapter>();
builder.Services.AddScoped<IBusySource>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());
builder.Services.AddScoped<ICalendarWriter>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());
builder.Services.AddScoped<IConferenceCreator>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());

builder.Services.AddScoped<CalendarSyncService>();

// Scoped, and registered here by its first consumer: it adds to the request's DbContext and
// never saves, so it has to be the same context the caller is about to commit.
builder.Services.AddScoped<OutboxWriter>();

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));

// The two halves of the outbox, and two hosted services rather than one. Noticing that a meeting
// ended and delivering what is queued are different jobs on different clocks: mixed into one
// service, a receiver that refuses would drag the noticing into its retry loop and every retry
// storm would become a duplicate storm. Singletons, as every hosted service is, so both reach
// the database through IServiceScopeFactory — a scope per tick, because DbContext is scoped, is
// not thread-safe, and these two tick independently of each other and of every request.
builder.Services.AddHostedService<MeetingEndedPoller>();
builder.Services.AddHostedService<OutboxDispatcher>();

builder.Services.AddValidation();
builder.Services.AddProblemDetails();

// Order is the contract: contention is answered first, then domain failures, and anything
// neither handler claims falls through to a 500 with its stack trace in the log.
builder.Services.AddExceptionHandler<DatabaseBusyExceptionHandler>();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<KairosDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStaticFiles();

// Versioned from day zero. The first breaking change is a second group mapped beside this one,
// not a migration every client has to make on the same afternoon.
var v1 = app.MapGroup(ApiVersions.V1);
v1.MapLinks();
v1.MapSlots();
v1.MapBookings();
v1.MapIntegrations(integrationAdminToken);

// Said out loud, once, at startup. An endpoint that hands out a consent URL and accepts the
// callback that binds a calendar to this instance is not something to leave open quietly.
if (string.IsNullOrEmpty(integrationAdminToken))
{
    app.Logger.LogWarning(
        "{Path} is not set: the integration endpoints are unauthenticated. Set it to a random "
        + "string and send it as the {Header} header, or keep this instance off the public "
        + "internet until Part C ships real authentication.",
        adminTokenPath,
        IntegrationEndpoints.AdminTokenHeader);
}

// Also said out loud, once. An unconfigured relay is a working Kairos — bookings succeed and
// nothing throws — but it is indistinguishable from a broken one from the outside, because the
// symptom of both is mail that never arrives. This is the line that tells the two apart.
if (!app.Services.GetRequiredService<IOptions<EmailOptions>>().Value.IsConfigured)
{
    app.Logger.LogWarning(
        "{Section} is not configured: bookings will be written and nobody will be emailed. Set "
        + $"{EmailOptions.SectionName}:Host, {EmailOptions.SectionName}:From and "
        + $"{EmailOptions.SectionName}:PublicBaseUrl to turn confirmations on.",
        EmailOptions.SectionName);
}

// React owns client routing, so anything that is not an API route and not a file on disk is the
// single page. Mapped last, so it can never shadow a route above it.
app.MapFallbackToFile("index.html");

app.Run();
