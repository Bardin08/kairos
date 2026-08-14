using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Links;
using Kairos.Api.Features.Slots;
using Kairos.Api.Persistence;
using Kairos.Api.Scheduling;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

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

// Scoped, not singleton: it reads the request's DbContext. SlotQuery takes IEnumerable<IBusySource>,
// so Part B's calendar source is one more registration here and no change anywhere else.
builder.Services.AddScoped<IBusySource, LocalBusySource>();
builder.Services.AddScoped<AvailabilityQuery>();
builder.Services.AddScoped<SlotQuery>();
builder.Services.AddScoped<BookingWriter>();

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

// React owns client routing, so anything that is not an API route and not a file on disk is the
// single page. Mapped last, so it can never shadow a route above it.
app.MapFallbackToFile("index.html");

app.Run();
