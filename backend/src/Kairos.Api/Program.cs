using System.Net;

using Kairos.Api.Availability;
using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Integrations;
using Kairos.Api.Features.Invites;
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

// Aliased because Google's factory interface collides by name with the one in System.Net.Http.
using GoogleHttpFactory = Google.Apis.Http.IHttpClientFactory;

// Aliased because HttpOverrides carries a deprecated IPNetwork of its own, while KnownIPNetworks —
// the property that replaced the deprecated KnownNetworks — takes the BCL one.
using IPNetwork = System.Net.IPNetwork;

// Aliased because ASP.NET Core's authentication namespace brings its own SystemClock, and the one
// this application means is the one every instant in it is read from.
using SystemClock = NodaTime.SystemClock;

// CreateSlimBuilder rather than CreateBuilder: it leaves out the configuration providers and
// logging this app never reads, which is startup time and a few Mi of a Raspberry Pi's memory a
// self-hosted single-organizer scheduler has better uses for.
var builder = WebApplication.CreateSlimBuilder(args);

// AddDbContext, not AddDbContextPool: KairosDbContext registers its pragma interceptor from
// OnConfiguring, and pooling refuses a context that configures itself — "'OnConfiguring' cannot
// be used to modify DbContextOptions when DbContext pooling is enabled", thrown at the first
// migration. The pragmas are not optional, so the pool is what gives way.
builder.Services.AddDbContext<KairosDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=kairos.db"));

builder.Services.AddSingleton<IClock>(SystemClock.Instance);

const string secretKeyPath = "Security:SecretKey";
const string hstsMaxAgeDaysPath = "Security:HstsMaxAgeDays";

// Long enough to be worth sending and short enough that a deployment which loses its certificate
// is not locked out of its own domain for a year.
const int defaultHstsMaxAgeDays = 30;

// Constructed here rather than resolved lazily, so an absent, non-base64 or wrong-length key
// stops the process at startup. An app that boots without its key and finds out at the first
// OAuth callback has turned a configuration error into a runtime mystery weeks later.
builder.Services.AddSingleton(new SecretProtector(
    builder.Configuration[secretKeyPath] ?? throw new InvalidOperationException(
        $"{secretKeyPath} is not configured. Kairos stores provider credentials encrypted at "
        + $"rest and needs a base64 {SecretProtector.KeyBytes}-byte AES-GCM key. Generate one "
        + $"with: openssl rand -base64 {SecretProtector.KeyBytes}")));

builder.Services.AddKeyRing();

// Stateless and sealed — one instance, on the hot path of every booking page.
builder.Services.AddSingleton<SlotGenerator>();

// Read here rather than bound to an options class because all three values are needed while the
// handlers are being configured, and read with a throw for the same reason the secret key above
// has one: an absent authority does not announce itself, it surfaces later as a redirect to
// nowhere.
var authority = RequiredForSignIn("Authority");
var clientId = RequiredForSignIn("ClientId");
var clientSecret = RequiredForSignIn("ClientSecret");

// Where the locked admin screen sends the operator, and the one optional Authentication value: it
// defaults to this application's own challenge route, which needs no provider name because the
// handler discovers everything from the authority above. Overridden only by a deployment that
// wants the operator to pass through something else first.
var signInUrl = builder.Configuration[SignInEndpoints.ConfigurationKey] ?? SignInEndpoints.DefaultUrl;

string RequiredForSignIn(string key) =>
    builder.Configuration[$"Authentication:{key}"] ?? throw new InvalidOperationException(
        $"Authentication:{key} is not configured. The admin surface is authenticated against an "
        + "OIDC provider, so Kairos needs its issuer URL and a client registered there.");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // Always, with no configuration knob: a deployment that can turn Secure off is a
        // deployment that will eventually be found with it off. Behind a TLS-terminating proxy the
        // scheme Kestrel sees is the forwarded one, which UseForwardedHeaders resolves before this
        // cookie is ever written.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;

        // Lax rather than Strict: the operator arrives back from the identity provider by
        // redirect, and Strict withholds the cookie on that first cross-site navigation — a
        // sign-in loop that reads as a broken provider rather than as a cookie policy.
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Everything behind the gate is an API route the admin surface fetches, and a fetch cannot
        // follow a redirect to another origin — it fails as an opaque CORS error saying nothing
        // about the session having expired. So the handler answers with status codes a client can
        // act on; the one redirect worth making is the login route below, which a browser navigates
        // to rather than fetches.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        // A body on the refusal, in the shape DomainExceptionHandler writes: a bodyless 403 leaves
        // the workbench nothing to tell "signed in, not an operator" from any other refusal, and
        // those screens have different remedies.
        //
        // Naming it HERE is correct only while AdminGroup.Policy is the only policy, so every forbid
        // this handler sees really is that one. A second policy would answer not_an_operator to a
        // refusal that is nothing of the kind, and this code would move to where the policy is
        // known.
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

        // Development only, gated so production cannot reach it however the configuration is
        // written: the local provider in compose.dev.yaml serves discovery and its JWKS over plain
        // HTTP on loopback. It relaxes the TRANSPORT the metadata is fetched over and nothing else —
        // signatures still verified against that JWKS, issuer and audience still checked. Not an
        // authentication bypass, and never to be widened into one.
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

// Google's own factory, which the client library would have used anyway. Registered rather than
// defaulted so a test can stand in front of the network without either Google class growing an
// interface whose only implementation would wrap this line.
builder.Services.AddSingleton<GoogleHttpFactory>(new Google.Apis.Http.HttpClientFactory());

// Registered twice, resolved once: the endpoints see an IIntegrationConnector and never a Google
// type, while Part B's calendar work asks for the factory by name. One instance either way,
// because the in-memory access token is only a cache if there is a single holder of it.
builder.Services.AddSingleton<GoogleClientFactory>();
builder.Services.AddSingleton<IIntegrationConnector>(
    services => services.GetRequiredService<GoogleClientFactory>());

// Scoped: it reads and writes through the request's DbContext.
builder.Services.AddScoped<IntegrationCredentialStore>();
builder.Services.AddScoped<IntegrationSettingStore>();

// The alert channel's first publisher. A second source is one more line here and no change to the
// route, the response or the panel — which is the whole reason the channel is an interface rather
// than a query the endpoint runs itself.
builder.Services.AddScoped<IOperatorAlertSource, IntegrationAlertSource>();

builder.Services.AddPublicBaseUrl(builder.Configuration);

// Stateless, every one of them, and holding nothing per request: the builders render text, the
// sender opens a connection per message, and the mailers only compose. The HTML renderer holds
// only the root provider it renders components from, and builds a fresh HtmlRenderer per message.
// Singletons, like SlotGenerator.
builder.Services.AddSingleton<IcsBuilder>();
builder.Services.AddSingleton<MailHtmlRenderer>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<BookingMailer>();
builder.Services.AddSingleton<OperatorMailer>();

// Scoped, not singleton: they read the request's DbContext. SlotQuery takes
// IEnumerable<IBusySource>, so the calendar source is one more line here and no change anywhere
// else — and a third provider would be a third line and still nothing in Scheduling/.
// Registered unconditionally: unconfigured and never-connected are answered inside the adapter,
// which returns no busy time rather than throwing, so a Kairos with no Google is a working Kairos.
builder.Services.AddScoped<IBusySource, LocalBusySource>();
builder.Services.AddScoped<AvailabilityQuery>();
builder.Services.AddScoped<SlotQuery>();
builder.Services.AddScoped<BookingWriter>();
builder.Services.AddScoped<BookingCanceller>();
builder.Services.AddScoped<BookingMover>();
builder.Services.AddScoped<ProposalWriter>();
builder.Services.AddScoped<InviteWriter>();
builder.Services.AddScoped<InviteReader>();
builder.Services.AddScoped<IntegrationProviderResolver>();
builder.Services.AddScoped<IntegrationSettingsReader>();
builder.Services.AddScoped<IntegrationSettingsWriter>();

// One adapter resolved through four interfaces, one instance per request rather than four:
// registering the class separately and handing the same instance to each role keeps a single Google
// client per scope. Busy sources are a collection because every source has a say; the writer and
// the conference creator are single because a meeting goes into one calendar. Settings are a
// collection because the reader picks the one whose Provider matches the route. A second provider
// is therefore a second IBusySource line beside this one and a replacement of the other two.
builder.Services.AddScoped<GoogleCalendarAdapter>();
builder.Services.AddScoped<IBusySource>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());
builder.Services.AddScoped<ICalendarWriter>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());
builder.Services.AddScoped<IConferenceCreator>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());
builder.Services.AddScoped<IIntegrationSettings>(
    services => services.GetRequiredService<GoogleCalendarAdapter>());

builder.Services.AddScoped<CalendarSyncService>();

// Scoped, beside the sync whose row it reads: the identity a notice quotes is committed inside
// that call, so this answers from the database rather than from anything a caller carries.
builder.Services.AddScoped<MeetingIdentityResolver>();

// Scoped, and registered here by its first consumer: it adds to the request's DbContext and
// never saves, so it has to be the same context the caller is about to commit.
builder.Services.AddScoped<OutboxWriter>();
builder.Services.AddScoped<FollowUpWriter>();

// A singleton because it is the process's one doorbell: a scoped one would give every request its
// own channel and nudge nobody.
builder.Services.AddSingleton<FollowUpSignal>();

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));

// Two hosted services rather than one. Noticing that a meeting ended and delivering what is queued
// are different jobs on different clocks: mixed into one service, a receiver that refuses would
// drag the noticing into its retry loop and every retry storm would become a duplicate storm.
// Singletons, as every hosted service is, so both reach the database through IServiceScopeFactory —
// a scope per tick, because DbContext is scoped, is not thread-safe, and these two tick
// independently of each other and of every request.
builder.Services.AddHostedService<MeetingEndedPoller>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<FollowUpWorker>();

// Beside them rather than inside that pair: it runs on the same minute and notices something only
// a clock can notice, but what it produces is a mail rather than a queued event. Nothing depends on
// it running. Availability never did — a hold whose expiry has passed has already stopped holding,
// in both conflict readers — and re-proposing no longer does either, because the propose route
// closes a lapsed hold itself and leaves the notice here. So a deployment where this never ticks
// has late mail, and a calendar and a workbench that are both correct.
builder.Services.AddHostedService<ProposalSweeper>();

// And beside that one, on the same minute and for the same kind of reason: it turns a clock into a
// mail. What it does not share is the tolerance for never running. A hold that lapses stops holding
// whether or not anything ticks, so ProposalSweeper only owes the operator a notice; a reminder
// exists only because this sends it, and a deployment where this never ticks is the feature absent
// rather than late. Late it survives — the due moment is a row, so a process down over one sends on
// the tick after it returns, right up until the meeting itself starts.
builder.Services.AddHostedService<ReminderSweeper>();

builder.Services.AddRateLimitPolicies();

builder.Services.AddHealthEndpoints();

// Which addresses may speak for a caller, and which scheme the request really arrived on. The
// address is what the rate limiter partitions on; without the scheme a TLS-terminating proxy leaves
// this application on plain HTTP and the OIDC handler builds an http:// redirect_uri the provider
// refuses.
//
// Left at the framework's default — loopback only — so a deployment NOT behind a proxy cannot be
// told a false client address. Clearing these lists to accept X-Forwarded-For from anybody hands
// every caller a free identity, and with it every other caller's rate limit.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // How many proxies deep the real caller sits. One is right for a single reverse proxy and
    // wrong for a tunnel in front of one: with two hops in the header and a limit of one, the
    // middleware stops at the nearer proxy and resolves every caller on earth to that one
    // address, which merges them into a single rate-limit bucket and says nothing while it does
    // it. Raising it is only safe with every trusted hop named below — an unnamed hop is how a
    // caller gets to prepend whatever address it likes.
    options.ForwardLimit = builder.Configuration.GetValue<int?>(ProxyOptions.ForwardLimitPath)
        ?? ProxyOptions.DefaultForwardLimit;

    var proxies = builder.Configuration.GetSection(ProxyOptions.KnownProxiesPath).Get<string[]>() ?? [];
    for (var index = 0; index < proxies.Length; index++)
    {
        options.KnownProxies.Add(
            Parsed(proxies[index], $"{ProxyOptions.KnownProxiesPath}:{index}", IPAddress.Parse));
    }

    var networks =
        builder.Configuration.GetSection(ProxyOptions.KnownNetworksPath).Get<string[]>() ?? [];
    for (var index = 0; index < networks.Length; index++)
    {
        // CIDR as written in a config file: "10.0.0.0/8".
        options.KnownIPNetworks.Add(
            Parsed(networks[index], $"{ProxyOptions.KnownNetworksPath}:{index}", IPNetwork.Parse));
    }
});

// The framework's own message for a bad address is "An invalid IP address was specified", naming
// neither the setting it came from nor the value that broke it — and this runs while the pipeline
// is built, so it is read by somebody watching a container refuse to start. Both parsers answer
// every malformed value with FormatException, an out-of-range prefix included; the argument shapes
// are caught as well, so a null is named the same way rather than escaping bare.
static T Parsed<T>(string value, string path, Func<string, T> parse)
{
    try
    {
        return parse(value);
    }
    catch (Exception failure) when (failure is FormatException or ArgumentException)
    {
        throw new InvalidOperationException(
            $"{path} contains '{value}', which is not a valid value for it.", failure);
    }
}

// Configuration only: nothing calls UseHsts, and UseSecurityHeaders reads these values and writes
// the header where a cleared response cannot lose it. AddHsts stays the one place to change them,
// and the one excluded-hosts list.
//
// Both flags off, written out because they are a decision. Kairos is deployed on somebody else's
// domain: IncludeSubDomains would pin sibling subdomains it does not own, and the preload list is
// close to irreversible and belongs to whoever owns the apex. An operator who wants either says so
// at their proxy.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(
        builder.Configuration.GetValue<int?>(hstsMaxAgeDaysPath) ?? defaultHstsMaxAgeDays);
    options.IncludeSubDomains = false;
    options.Preload = false;
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
// limiter's partition, and the redirect_uri a challenge builds — reads the wrong one otherwise.
app.UseForwardedHeaders();

// Ahead of everything that writes a response — the static files short circuit and the exception
// handler's cleared response included. HSTS is one of the headers it writes, and there is
// deliberately no UseHsts: that middleware writes eagerly and loses its header on the exception
// path, which SecurityHeaders.IsSecure records with the measurement.
//
// No UseHttpsRedirection either. The proxy terminates TLS and every request reaches this process
// over plaintext, so the middleware would do nothing or redirect every request to itself. The edge
// holds the certificate, so the edge redirects.
app.UseSecurityHeaders();

app.UseExceptionHandler();
app.UseStaticFiles();

// Written out rather than left to the automatic registration, because which requests carry an
// identity and where the gate sits is not something a reader of this file should have to know a
// framework convention to answer.
app.UseAuthentication();
app.UseAuthorization();

// After routing, because the endpoint's own metadata is what says whether it carries a budget.
app.UseRateLimiting();

// Anonymous by necessity: a challenge is a browser navigation to another origin, so nothing behind
// the gate could ever start one. Two of the six routes outside the admin prefix, which AdminGroup
// enumerates. The admin surface sends the window here after a 401; there is no login form anywhere
// in this application.
app.MapGet("/admin/login", () => Results.Challenge(
    new AuthenticationProperties { RedirectUri = "/admin" },
    [OpenIdConnectDefaults.AuthenticationScheme]));

// The Kairos session, and not the Pocket ID one. Ending both is a second redirect and a second
// URL to register with the provider, to log the operator out of everything else they were using
// it for — which is not what pressing sign out on a scheduling app asks for.
app.MapGet("/admin/logout", () => Results.SignOut(
    new AuthenticationProperties { RedirectUri = "/" },
    [CookieAuthenticationDefaults.AuthenticationScheme]));

// What an orchestrator asks before it sends anybody here. Outside the versioned prefix
// deliberately: a probe path is written into a deployment manifest that nobody re-reads, so a
// version bump must not be able to move it out from under a running cluster.
app.MapHealth();

var v1 = app.MapGroup(ApiVersions.V1);
v1.MapLinks();
v1.MapSlots();
v1.MapBookings();
v1.MapInvites();
// Anonymous, and the whole reason it exists: the operator reading it has just been refused. It
// answers one string — where to sign in — so the locked screen can draw a link without the URL
// being baked into a bundle that is built once and deployed everywhere.
v1.MapSignIn(signInUrl);
// The OAuth return leg, and the only route here that is not an invitee's. It is public because
// the browser arriving on it holds a provider's redirect rather than a session of ours, and its
// path is registered with that provider — so it is the one integration route that stays outside
// the admin prefix. The other two moved under it.
v1.MapIntegrationCallback();

// Mapped through MapAdmin and never through v1, because that call is the only thing standing
// between an anonymous caller and every one of these routes. One group, chained: a second
// MapAdmin() would be a second place to read when asking what is public.
app.MapAdmin()
    .MapLinksAdmin()
    .MapInvitesAdmin()
    .MapMeetingsAdmin()
    .MapPatternsAdmin()
    .MapSettingsAdmin()
    .MapIntegrationsAdmin()
    .MapIntegrationSettingsAdmin()
    .MapAlertsAdmin();

// Said out loud because the middleware cannot say it. A RequireAssertion has no logger and
// AuthorizationMiddleware reports only "requirements were not met: AssertionRequirement", so the
// likeliest way this boundary goes wrong in production — a provider that releases no groups claim —
// reaches an operator as a 403 with nothing anywhere to read. This is what was expected.
app.Logger.LogInformation(
    "The admin surface admits accounts whose {ClaimType} claim carries {Group}, narrowed to "
    + "{SubjectCount} allowed subjects — zero meaning the group alone decides.",
    AdminAuthorization.GroupClaimType,
    adminGroup,
    adminSubjects.Count);

// The same, about a boundary that fails even more quietly. A depth too small for the topology is
// not an error anywhere: the middleware stops at the nearest proxy, every caller on earth resolves
// to that one address, and they share a single rate-limit partition. Nothing in a response says so,
// and RateLimitBudget's warning cannot see it — that one fires on a connection with no peer at all.
// So the numbers are stated at boot, where an operator can hold them against their own topology.
var forwarding = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
app.Logger.LogInformation(
    "Forwarded headers are resolved {ForwardLimit} hop(s) deep, trusting {ProxyCount} addresses "
    + "and {NetworkCount} networks — loopback is one of each, so 1 and 1 is a deployment "
    + "that named none. A tunnel in front of the proxy is a second hop: raise "
    + $"{ProxyOptions.ForwardLimitPath}, and name every hop in {ProxyOptions.KnownProxiesPath}.",
    forwarding.ForwardLimit,
    forwarding.KnownProxies.Count,
    forwarding.KnownIPNetworks.Count);

// An unconfigured relay is a working Kairos — bookings succeed and nothing throws — but from the
// outside it is indistinguishable from a broken one, because the symptom of both is mail that never
// arrives. This line tells the two apart.
if (!app.Services.GetRequiredService<IOptions<EmailOptions>>().Value.IsConfigured)
{
    app.Logger.LogWarning(
        "{Section} is not configured: bookings will be written and nobody will be emailed. Set "
        + $"{EmailOptions.SectionName}:Host, {EmailOptions.SectionName}:From, and a public base "
        + $"URL — {AppOptions.SectionName}:PublicBaseUrl, or {EmailOptions.SectionName}:PublicBaseUrl "
        + "to override it for mail alone — to turn confirmations on.",
        EmailOptions.SectionName);
}

// The API prefix ends here. Without this line the fallback below claims every extensionless path,
// /api included, so an unmapped API route answered 200 with the single page — a mistyped admin path
// looked authorized and reached a fetch as HTML that failed on a JSON parse error rather than as
// the 404 it was. Precedence is deterministic rather than lucky: a literal segment beats a
// wildcard, so every route mapped above still wins this one.
app.Map("/api/{**rest}", () => Results.NotFound());

// React owns client routing, so anything that is not an API route and not a file on disk is the
// single page. Mapped last, so it can never shadow a route above it.
app.MapFallbackToFile("index.html");

app.Run();
