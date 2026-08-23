using System.Diagnostics;

using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// The poller against a real SQLite database, run as the host runs it. What is pinned is which
/// bookings it picks up — over and confirmed, nothing else — and that an already-announced
/// meeting costs it nothing but a rejected insert.
/// </summary>
/// <remarks>
/// Nothing is substituted: the service resolves its own scope, context and writer from a
/// container built the way the application builds one. Delivery is verified against a real
/// listener in <see cref="OutboxDispatcher"/>'s tests, an HTTP stub proving nothing.
/// </remarks>
public class MeetingEndedPollerTests
{
    /// <summary>Generous, because a slow CI runner must not fail a test about correctness.</summary>
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private const int OneHourSeconds = 3600;
    private const int OneDaySeconds = 86400;

    [Fact]
    public async Task Sweep_AnnouncesTheMeetingThatEnded_AndNothingElse()
    {
        using var database = new TestDatabase();
        var now = Now();
        var linkId = SeedLink(database);

        // Three bookings, one of which is the answer. The future meeting is the time half of the
        // predicate and the cancelled one is the status half; a poller that dropped either would
        // announce a meeting that has not happened or one that never will.
        var ended = AddBooking(database, linkId, now - OneDaySeconds, BookingStatus.Confirmed);
        AddBooking(database, linkId, now + OneDaySeconds, BookingStatus.Confirmed);
        AddBooking(database, linkId, now - OneDaySeconds, BookingStatus.Cancelled);

        await using var provider = BuildContainer(database);
        await SweepAsync(database, provider, 1);

        using var context = database.CreateContext();
        var messages = context.OutboxMessages.AsNoTracking().ToList();
        messages.Count.ShouldBe(1);
        messages[0].BookingId.ShouldBe(ended);
        messages[0].Type.ShouldBe(OutboxMessageType.MeetingEnded);
        messages[0].Payload.ShouldContain(BookingEventPayload.MeetingEnded);

        // Enqueued and nothing more. Delivery is the dispatcher's job, and a poller that had
        // touched either column would be doing it.
        messages[0].DispatchedAt.ShouldBeNull();
        messages[0].Attempts.ShouldBe(0);
    }

    /// <summary>
    /// The second tick, and the reason the poller runs no "have I already announced this?" query.
    /// The already-announced meeting is deliberately the older, so its rejected insert happens
    /// first: a sweep stopped by the rejection would never reach the meeting that needs it.
    /// </summary>
    [Fact]
    public async Task Sweep_MeetingAlreadyAnnounced_SwallowsTheDuplicateAndAnnouncesTheRest()
    {
        using var database = new TestDatabase();
        var now = Now();
        var linkId = SeedLink(database);
        var announced = AddBooking(database, linkId, now - (2 * OneDaySeconds), BookingStatus.Confirmed);

        await using var provider = BuildContainer(database);
        await SweepAsync(database, provider, 1);

        var second = AddBooking(database, linkId, now - OneDaySeconds, BookingStatus.Confirmed);
        await SweepAsync(database, provider, 2);

        using var context = database.CreateContext();
        var messages = context.OutboxMessages.AsNoTracking().ToList();
        messages.Count.ShouldBe(2);
        messages.Count(message => message.BookingId == announced).ShouldBe(1);
        messages.Count(message => message.BookingId == second).ShouldBe(1);
    }

    /// <summary>
    /// One sweep by one fresh instance — the service sweeps at startup, so starting it is how a
    /// tick is asked for — waited out by the rows it was started to write, then stopped so that
    /// nothing is still running when the assertions read the table.
    /// </summary>
    private static async Task SweepAsync(TestDatabase database, ServiceProvider provider, int expected)
    {
        var poller = new MeetingEndedPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            SystemClock.Instance,
            NullLogger<MeetingEndedPoller>.Instance);

        await poller.StartAsync(TestContext.Current.CancellationToken);
        await WaitForMessagesAsync(database, expected);
        await poller.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The container the poller resolves through, built the way the application builds one:
    /// options constructed by hand and registered as a singleton, so the process keeps one EF
    /// model. AddDbContext would mint a second, which the suite's compiled queries fail against.
    /// </summary>
    private static ServiceProvider BuildContainer(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddSingleton(
            new DbContextOptionsBuilder<KairosDbContext>().UseSqlite(database.ConnectionString).Options);
        services.AddScoped<KairosDbContext>();
        services.AddScoped<OutboxWriter>();
        services.AddSingleton<IClock>(SystemClock.Instance);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Polls rather than sleeps. A hosted service starts asynchronously, so the only honest
    /// signal that its sweep has run is the row it was started to write.
    /// </summary>
    private static async Task WaitForMessagesAsync(TestDatabase database, int expected)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < SweepBudget)
        {
            using (var context = database.CreateContext())
            {
                if (context.OutboxMessages.AsNoTracking().Count() >= expected)
                {
                    return;
                }
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(
            $"The poller wrote fewer than {expected} outbox messages within "
            + $"{SweepBudget.TotalSeconds:0} seconds.");
    }

    private static long Now() => SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds();

    private static Guid SeedLink(TestDatabase database)
    {
        using var context = database.CreateContext();

        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = "consulting",
            Name = "Consulting",
            PatternId = pattern.Id,
        };
        context.Links.Add(link);

        context.SaveChanges();
        return link.Id;
    }

    private static Guid AddBooking(
        TestDatabase database,
        Guid linkId,
        long startUtc,
        BookingStatus status)
    {
        using var context = database.CreateContext();

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = linkId,
            StartUtc = startUtc,
            EndUtc = startUtc + OneHourSeconds,
            Status = status,
            ManageTokenHash = Guid.CreateVersion7().ToString("N"),
            CreatedAt = startUtc,
        };

        context.Bookings.Add(booking);
        context.SaveChanges();
        return booking.Id;
    }
}
