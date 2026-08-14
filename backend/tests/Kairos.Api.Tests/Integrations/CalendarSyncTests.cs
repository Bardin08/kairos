using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// What a booking is left holding after the providers have had their turn. Every assertion here
/// is a row in the database, because those rows are the entire product of this class — Part C's
/// Meetings tab reads them and nothing else, and an operator's answer to "is my calendar
/// working?" is whatever they say.
/// </summary>
/// <remarks>
/// The two providers are stubs rather than mocks and nothing counts calls on them. What is
/// pinned is the three outcomes a real provider can produce — no account, a refusal, an answer —
/// and the state each has to leave behind. A test asserting that <c>Events.Insert</c> was invoked
/// would pass against a Google client that had been broken in every way that matters.
/// </remarks>
public class CalendarSyncTests
{
    private const string EventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";
    private const string ConferenceId = "abc-defg-hij";
    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";
    private const string Refusal = "Google said no.";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    /// <summary>
    /// The decision this task had to make, pinned so it cannot be undone by accident: a
    /// deployment that never connected a calendar gets no rows. A <c>Failed</c> row would be a
    /// permanent red mark on every booking of a Kairos that was never asked to sync, and a signal
    /// that fires on every row is not a signal.
    /// </summary>
    [Fact]
    public async Task SyncAsync_NoProviderIsConnected_RecordsNothingAtAll()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);

        await Service(context, new StubCalendar { Connected = false }, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        context.CalendarEntries.AsNoTracking().ShouldBeEmpty();
        context.ConferenceLinks.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// Ground rule 14. The provider throws, the call returns, and the reason survives in a row
    /// somebody can read — the difference between a recorded failure and a swallowed one.
    /// </summary>
    [Fact]
    public async Task SyncAsync_TheCalendarRefuses_RecordsFailedWithTheReasonAndReturns()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);

        var calendar = new StubCalendar { Failure = new InvalidOperationException(Refusal) };
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Failed);
        entry.LastError.ShouldBe(Refusal);
        entry.ExternalEventId.ShouldBeNull();
        context.ConferenceLinks.AsNoTracking().ShouldBeEmpty();
    }

    [Fact]
    public async Task SyncAsync_BothProvidersAnswer_RecordsTheEventAndTheJoinUrl()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);

        await Service(context, new StubCalendar(), new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Synced);
        entry.ExternalEventId.ShouldBe(EventId);
        entry.LastError.ShouldBeNull();

        var link = context.ConferenceLinks.AsNoTracking().Single();
        link.ExternalId.ShouldBe(ConferenceId);
        link.JoinUrl.ShouldBe(JoinUrl);
    }

    /// <summary>
    /// The two halves fail independently, which is why <see cref="ConferenceLink"/> is its own
    /// table. An event that is genuinely in the calendar must not be reported as a failed sync
    /// because the conferencing half of the same provider had nothing to give.
    /// </summary>
    [Fact]
    public async Task SyncAsync_TheConferenceFails_LeavesTheEventSyncedWithNoLink()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);

        var conference = new StubConference { Failure = new InvalidOperationException(Refusal) };
        await Service(context, new StubCalendar(), conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
        context.ConferenceLinks.AsNoTracking().ShouldBeEmpty();
    }

    private static CalendarSyncService Service(
        KairosDbContext context,
        ICalendarWriter calendars,
        IConferenceCreator conferences) =>
        new(
            calendars,
            conferences,
            context,
            SystemClock.Instance,
            NullLogger<CalendarSyncService>.Instance);

    /// <summary>
    /// Returned with its link and participants loaded, because that is the state the create
    /// endpoint hands the service a booking in.
    /// </summary>
    private static Booking Seed(KairosDbContext context)
    {
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

        var bookingId = Guid.CreateVersion7();
        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            LinkId = link.Id,
            StartUtc = MeetingStartUtc,
            EndUtc = MeetingEndUtc,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = "hash",
            CreatedAt = MeetingStartUtc,
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    BookingId = bookingId,
                    Role = ParticipantRole.Booker,
                    Name = "Ada",
                    Email = "ada@example.com",
                    TimeZoneId = "Europe/Warsaw",
                },
            ],
        });

        context.SaveChanges();

        return context.Bookings
            .Include(booking => booking.Link)
            .Include(booking => booking.Participants)
            .Single();
    }

    private sealed class StubCalendar : ICalendarWriter
    {
        public bool Connected { get; init; } = true;

        public Exception? Failure { get; init; }

        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Connected);

        public Task<string> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(EventId) : Task.FromException<string>(Failure);

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubConference : IConferenceCreator
    {
        public Exception? Failure { get; init; }

        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<ConferenceDetails> CreateAsync(
            Guid bookingId,
            CancellationToken cancellationToken) =>
            Failure is null
                ? Task.FromResult(new ConferenceDetails(ConferenceId, new Uri(JoinUrl)))
                : Task.FromException<ConferenceDetails>(Failure);
    }
}
