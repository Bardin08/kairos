using System.Net;

using Kairos.Api.Availability;
using Kairos.Api.Features.Admin;
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

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using NodaTime;

// Aliased because HttpOverrides carries a deprecated IPNetwork of its own, while KnownIPNetworks —
// the property that replaced the deprecated KnownNetworks — takes the BCL one.
using IPNetwork = System.Net.IPNetwork;

// Aliased because ASP.NET Core's authentication namespace brings its own SystemClock, and the one
// this application means is the one every instant in it is read from.
using SystemClock = NodaTime.SystemClock;

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
const string knownProxiesPath = "Proxy:KnownProxies";
const string knownNetworksPath = "Proxy:KnownNetworks";

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

// The identity provider, and with it the whole of the operator's authentication. Read here rather
// than bound to an options class because all three values are needed while the handlers are being
// configured, and read with a throw for the same reason the secret key above has one: an absent
// authority does not announce itself, it surfaces later as a redirect to nowhere.
var authority = RequiredForSignIn("Authority");
var clientId = RequiredForSignIn("ClientId");
var clientSecret = RequiredForSignIn("ClientSecret");

// Where the locked admin screen sends the operator, and the one Authentication value that is
// optional: it defaults to this application's own challenge route, which needs no provider name
// because the handler discovers everything from the authority above. Overridden only by a
// deployment that wants the operator to pass through something else first.
var signInUrl = builder.Configuration[SignInEndpoints.ConfigurationKey] ?? SignInEndpoints.DefaultUrl;

string RequiredForSignIn(string key) =>
    builder.Configuration[$"Authentication:{key}"] ?? throw new InvalidOperationException(
        $"Authentication:{key} is not configured. The admin surface is authenticated against an "
        + "OIDC provider, so Kairos needs its issuer URL and a client registered there.");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Everything behind the gate is an API route the admin surface fetches, and a fetch
        // cannot follow a redirect to another origin — it fails as an opaque CORS error that says
        // nothing about the session having expired. So the cookie handler answers with the status
        // codes a client can act on, and the one redirect worth making is the login route below,
        // which a browser navigates to rather than fetches.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        // A body on the refusal, in the shape DomainExceptionHandler writes, because a bodyless
        // 403 leaves the workbench nothing to tell "you are signed in and not an operator" apart
        // from any other refusal — and those two screens have different remedies.
        //
        // Naming that refusal HERE is correct only while AdminGroup.Policy is the only policy in
        // the application, so every forbid this handler sees really is that one. A second policy
        // would start answering not_an_operator to a refusal that is nothing of the kind, and the
        // code would have to move to where the policy that produced it is known.
        options.Events.OnRedirectToAccessDenied = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            await context.HttpContext.RequestServices
                .GetRequiredService<IProblemDetailsService>()
                .TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status403Forbidden,
                        Title = AdminAuthorization.NotAnOperatorTitle,
                        Extensions = { [ProblemCode.Key] = AdminAuthorization.NotAnOperatorCode },
                    },
                });
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;

        // The handler still defaults to the implicit id_token flow, which is what having a client
        // secret at all is an argument against. Code flow, with the PKCE the handler adds to it.
        options.ResponseType = OpenIdConnectResponseType.Code;

        // Without this the provider has no reason to put the group in the token, and every
        // account fails the policy below with nothing in the logs to say why.
        options.Scope.Add(AdminAuthorization.GroupScope);

        // Development only, and gated so that production cannot reach it however the
        // configuration is written. The local provider in compose.dev.yaml serves discovery and
        // its JWKS over plain HTTP on loopback, and the handler refuses to fetch either without
        // this. It relaxes the TRANSPORT the metadata is fetched over and nothing else: the
        // signature on every token is still verified against that JWKS, the issuer and audience
        // are still checked, and no request skips authentication because of this line. It is not
        // an authentication bypass and must never be widened into one.
        if (builder.Environment.IsDevelopment())
        {
            options.RequireHttpsMetadata = false;
        }
    });

// Read here rather than inside the assertion, so a blanked group stops the process at startup
// instead of failing every request in the same way an unauthorized caller does.
var adminGroup = AdminAuthorization.RequiredGroup(builder.Configuration);
var adminSubjects = AdminAuthorization.AllowedSubjects(builder.Configuration);

builder.Services.AddAuthorization(options =>
    options.AddPolicy(AdminGroup.Policy, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            AdminAuthorization.IsOperator(context.User, adminGroup, adminSubjects))));

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

builder.Services.AddRateLimitPolicies();

// Which addresses may speak for a caller, and which scheme the request really arrived on. Both
// matter today: the address is what the rate limiter partitions on, and without the scheme a
// TLS-terminating proxy leaves this application believing every request is plain HTTP — the OIDC
// handler then builds an http:// redirect_uri the provider refuses, and the cookie policy's
// SameAsRequest leaves Secure off the session cookie.
//
// Left at the framework's default — loopback only — so a deployment that is NOT behind a proxy
// cannot be told a false client address by a header. A deployment that is behind one names it, and
// clearing these lists to accept X-Forwarded-For from anybody would hand every caller a free
// identity and with it every other caller's rate limit.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxy in builder.Configuration.GetSection(knownProxiesPath).Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in builder.Configuration.GetSection(knownNetworksPath).Get<string[]>() ?? [])
    {
        // CIDR as written in a config file: "10.0.0.0/8".
        options.KnownIPNetworks.Add(IPNetwork.Parse(network));
    }
});

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

// Before everything, because every line below it that reads a scheme or an address — the rate
// limiter's partition, and the cookie policy the proxy task adds — reads the wrong one otherwise.
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStaticFiles();

// Written out rather than left to the automatic registration, because which requests carry an
// identity and where the gate sits is not something a reader of this file should have to know a
// framework convention to answer.
app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();
app.UseRateLimitHeaders();

// The two routes that make a session and end one, and the only two that are anonymous by
// necessity rather than by choice: a challenge is a browser navigation to another origin, so
// nothing behind the gate could ever start one. The admin surface sends the window here after a
// 401 and there is no login form anywhere in this application.
app.MapGet("/admin/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/admin" },
    [OpenIdConnectDefaults.AuthenticationScheme]));

// The Kairos session, and not the Pocket ID one. Ending both is a second redirect and a second
// URL to register with the provider, to log the operator out of everything else they were using
// it for — which is not what pressing sign out on a scheduling app asks for.
app.MapGet("/admin/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    [CookieAuthenticationDefaults.AuthenticationScheme]));

// Versioned from day zero. The first breaking change is a second group mapped beside this one,
// not a migration every client has to make on the same afternoon.
var v1 = app.MapGroup(ApiVersions.V1);
v1.MapLinks();
v1.MapSlots();
v1.MapBookings();
// Anonymous, and the whole reason it exists: the operator reading it has just been refused. It
// answers one string — where to sign in — so the locked screen can draw a link without the URL
// being baked into a bundle that is built once and deployed everywhere.
v1.MapSignIn(signInUrl);
// The OAuth return leg, and the only route here that is not an invitee's. It is public because
// the browser arriving on it holds a provider's redirect rather than a session of ours, and its
// path is registered with that provider — so it is the one integration route that stays outside
// the admin prefix. The other two moved under it.
v1.MapIntegrationCallback();

// The operator's workbench. Mapped through MapAdmin and never through v1, because that call is
// the only thing standing between an anonymous caller and every one of these routes. One group,
// chained: a second MapAdmin() beside this one would be a second place to read when asking what
// is public, which is the question this shape exists to keep answerable in one line.
app.MapAdmin()
    .MapLinksAdmin()
    .MapMeetingsAdmin()
    .MapPatternsAdmin()
    .MapSettingsAdmin()
    .MapIntegrationsAdmin();

// Said out loud, once, because the middleware cannot say it. A RequireAssertion has no logger and
// AuthorizationMiddleware reports only "requirements were not met: AssertionRequirement", so the
// likeliest way this boundary goes wrong in production — a provider that releases no groups claim
// — reaches an operator as a 403 with nothing anywhere to read. This is what was expected.
app.Logger.LogInformation(
    "The admin surface admits accounts whose {ClaimType} claim carries {Group}, narrowed to "
    + "{SubjectCount} allowed subjects — zero meaning the group alone decides.",
    AdminAuthorization.GroupClaimType,
    adminGroup,
    adminSubjects.Count);

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

// The API prefix ends here. Without this line the fallback below claims every extensionless path,
// /api included, so an unmapped API route answered 200 with the single page — a mistyped admin
// path looked authorized and reached a fetch as HTML that failed on a JSON parse error rather than
// as the 404 it was. Precedence is deterministic rather than lucky: a literal segment beats a
// wildcard, so every route mapped above still wins this one.
app.Map("/api/{**rest}", () => Results.NotFound());

// React owns client routing, so anything that is not an API route and not a file on disk is the
// single page. Mapped last, so it can never shadow a route above it.
app.MapFallbackToFile("index.html");

app.Run();
