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
/// The two providers are a fake and a stub rather than mocks, and nothing counts calls on them.
/// What is pinned is the outcomes a real provider can produce — no account, a refusal, an answer
/// — and the state each has to leave behind. A test asserting that <c>Events.Insert</c> or
/// <c>Events.Update</c> was invoked would pass against a Google client that had been broken in
/// every way that matters; the fake holds the events it is given instead, so a move can be told
/// from a delete-and-recreate by looking at what the calendar ends up holding.
/// </remarks>
public class CalendarSyncTests
{
    private const string EventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";
    private const string ConferenceId = "abc-defg-hij";
    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";
    private const string Refusal = "Google said no.";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    /// <summary>The same hour, two hours later — a move, so the duration is unchanged.</summary>
    private const long MovedStartUtc = 1_788_007_200;
    private const long MovedEndUtc = 1_788_010_800;

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

        await Service(context, new FakeCalendar { Connected = false }, new StubConference())
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

        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
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

        await Service(context, new FakeCalendar(), new StubConference())
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
        await Service(context, new FakeCalendar(), conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
        context.ConferenceLinks.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// The whole of gap 1, in one assertion: after a move the calendar holds one event, under the
    /// id it always had, at the time the booking now claims. A second event would be a second
    /// invitation to everybody who had already accepted; the same event left at the old time is
    /// the invitee turning up an hour early.
    /// </summary>
    [Fact]
    public async Task RescheduleAsync_TheBookingMoved_MovesTheSameEventRatherThanReplacingIt()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        await MoveAsync(context, booking);
        await service.RescheduleAsync(booking, TestContext.Current.CancellationToken);

        var held = calendar.Events.ShouldHaveSingleItem();
        held.Key.ShouldBe(EventId);
        held.Value.Start.ToUnixTimeSeconds().ShouldBe(MovedStartUtc);
        held.Value.End.ToUnixTimeSeconds().ShouldBe(MovedEndUtc);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Synced);
        entry.ExternalEventId.ShouldBe(EventId);

        // The conference survives the move, which is the other half of not recreating: the invitee
        // is holding this URL already, and a new event would have come with a different one.
        context.ConferenceLinks.AsNoTracking().Single().JoinUrl.ShouldBe(JoinUrl);
    }

    /// <summary>
    /// The decision not to repair, pinned so it is a choice rather than an accident. A booking
    /// that was never written to a calendar is not placed into one by a reschedule: that would put
    /// a meeting in front of the organizer that the booker was never told was there, from a code
    /// path nobody was looking at. The absent state on the booking read is where the gap shows.
    /// </summary>
    [Fact]
    public async Task RescheduleAsync_NothingWasEverPlaced_LeavesTheCalendarAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();

        await MoveAsync(context, booking);
        await Service(context, calendar, new StubConference())
            .RescheduleAsync(booking, TestContext.Current.CancellationToken);

        calendar.Events.ShouldBeEmpty();
        context.CalendarEntries.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// Ground rule 14 on the move. The provider refuses, the call returns, and the event id stays
    /// on the row — it is the only handle anybody has for going and fixing the stale event by
    /// hand, and clearing it would leave a failure nobody can act on.
    /// </summary>
    [Fact]
    public async Task RescheduleAsync_TheMoveIsRefused_RecordsFailedAndKeepsTheEventIdToFixItBy()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        await MoveAsync(context, booking);
        calendar.Failure = new InvalidOperationException(Refusal);

        await Should.NotThrowAsync(
            () => service.RescheduleAsync(booking, TestContext.Current.CancellationToken));

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Failed);
        entry.LastError.ShouldBe(Refusal);
        entry.ExternalEventId.ShouldBe(EventId);
    }

    /// <summary>
    /// The other direction, and the reason the success path clears <c>LastError</c> rather than
    /// only setting the state: a row that keeps reporting a refusal it has since recovered from
    /// sends an operator looking for a fault that no longer exists.
    /// </summary>
    [Fact]
    public async Task RescheduleAsync_AMoveSucceedsAfterOneFailed_StopsReportingTheOldReason()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        calendar.Failure = new InvalidOperationException(Refusal);
        await service.RescheduleAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        await MoveAsync(context, booking);
        await service.RescheduleAsync(booking, TestContext.Current.CancellationToken);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Synced);
        entry.LastError.ShouldBeNull();
    }

    /// <summary>
    /// Gap 2. The event leaves the calendar, and the row that says it was ever there stays — with
    /// the id on it, because a withdrawn meeting and one that never synced are the two states an
    /// operator most needs told apart, and deleting the row makes them identical.
    /// </summary>
    [Fact]
    public async Task CancelAsync_TheEventIsWithdrawn_KeepsTheRowAsCancelledWithItsEventId()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        await service.CancelAsync(booking.Id, TestContext.Current.CancellationToken);

        calendar.Events.ShouldBeEmpty();

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Cancelled);
        entry.ExternalEventId.ShouldBe(EventId);
        entry.LastError.ShouldBeNull();

        // Left exactly as it was. The booking's own status already answers whether this meeting is
        // happening, and what this row records — that a conference was provisioned — stays true.
        context.ConferenceLinks.AsNoTracking().Single().JoinUrl.ShouldBe(JoinUrl);
    }

    /// <summary>
    /// Ground rule 14 on the withdrawal, and the lie it would otherwise tell. A refused delete
    /// leaves the event sitting in the calendar, so the row must say Failed: Cancelled would
    /// assert that an event which is still there is gone, and nobody would ever look at it again.
    /// </summary>
    [Fact]
    public async Task CancelAsync_TheWithdrawalIsRefused_DoesNotClaimTheEventIsGone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        calendar.Failure = new InvalidOperationException(Refusal);

        await Should.NotThrowAsync(
            () => service.CancelAsync(booking.Id, TestContext.Current.CancellationToken));

        calendar.Events.ShouldContainKey(EventId);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Failed);
        entry.LastError.ShouldBe(Refusal);
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
    /// The booking as the reschedule endpoint hands it over: already at its new time, already
    /// confirmed there, and already committed. Nothing about the release the endpoint performs on
    /// the way is visible here, because the calendar is never told about it.
    /// </summary>
    private static Task MoveAsync(KairosDbContext context, Booking booking)
    {
        booking.StartUtc = MovedStartUtc;
        booking.EndUtc = MovedEndUtc;
        booking.Sequence++;
        return context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

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

    /// <summary>
    /// A calendar, not a call recorder. It holds the events it is asked to hold and hands them
    /// back, so every assertion above reads what an organizer would see rather than which methods
    /// ran. A test that asserted <c>UpdateEventAsync</c> was invoked would pass against an
    /// implementation that deleted the event and made a new one — which is the single thing a
    /// move must never do, and the thing only the held state can tell you about.
    /// </summary>
    private sealed class FakeCalendar : ICalendarWriter
    {
        private readonly Dictionary<string, CalendarEventRequest> _events = [];

        public bool Connected { get; init; } = true;

        /// <summary>Settable, not init-only: the provider that answers and then stops is a state.</summary>
        public Exception? Failure { get; set; }

        public IntegrationProvider Provider => IntegrationProvider.Google;

        /// <summary>What this calendar is holding, keyed by the id it gave each event.</summary>
        public IReadOnlyDictionary<string, CalendarEventRequest> Events => _events;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Connected);

        public Task<string> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException<string>(Failure);
            }

            _events[EventId] = request;
            return Task.FromResult(EventId);
        }

        public Task UpdateEventAsync(
            string externalEventId,
            CalendarEventRequest request,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            // An id this calendar has never issued is the shape a delete-and-recreate leaves
            // behind, and a real provider answers it with a 404 rather than by inventing an event.
            if (!_events.ContainsKey(externalEventId))
            {
                return Task.FromException(new KeyNotFoundException(externalEventId));
            }

            _events[externalEventId] = request;
            return Task.CompletedTask;
        }

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            _events.Remove(externalEventId);
            return Task.CompletedTask;
        }
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
