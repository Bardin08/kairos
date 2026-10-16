using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// What a booking is left holding after the providers have had their turn. Most of what is
/// pinned is a row in the database, because those rows are this class's entire product: Part C's
/// Meetings tab reads them and nothing else.
/// </summary>
/// <remarks>
/// The two providers are a fake and a stub. Outcomes and the state each leaves behind are what
/// most tests pin rather than calls — asserting <c>Events.Update</c> was invoked would pass
/// against a client broken in every way that matters, where the fake's held events tell a move
/// from a delete-and-recreate. The retry tests are the exception: <c>Creates</c> and
/// <c>Deletes</c> exist because a second insert or delete under the same id is invisible in the
/// held state — it overwrites or removes what the first call left, and looks like one call
/// rather than two.
/// </remarks>
public class CalendarSyncTests
{
    private const string EventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";

    /// <summary>What Google derives a UID as, and the shape the .ics has to quote back.</summary>
    private const string CalendarUid = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0@google.com";
    private const string ConferenceId = "abc-defg-hij";
    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";
    private const string Refusal = "Google said no.";

    /// <summary>Stands where ciphertext would; nothing in this file decrypts a credential.</summary>
    private const string UnopenedPayload = "not-a-real-payload";

    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    /// <summary>The same hour, two hours later — a move, so the duration is unchanged.</summary>
    private const long MovedStartUtc = 1_788_007_200;
    private const long MovedEndUtc = 1_788_010_800;

    private const long OneDaySeconds = 86_400;

    /// <summary>A day before the meeting, so the seeded booking is upcoming.</summary>
    private static readonly Instant Now = Instant.FromUnixTimeSeconds(MeetingStartUtc - OneDaySeconds);

    /// <summary>A day before <see cref="Now"/>: a meeting already under way or over.</summary>
    private const long StartedUtc = MeetingStartUtc - (2 * OneDaySeconds);

    /// <summary>Spacing between seeded meetings, so their order is unambiguous.</summary>
    private const long StartStepSeconds = 3_600;

    private const int OneMeeting = 1;
    private const int NoMeetings = 0;

    /// <summary>
    /// The decision this task had to make, pinned so it cannot be undone by accident: a deployment
    /// that never connected a calendar gets no rows. A <c>Failed</c> row would be a permanent red
    /// mark on every booking of a Kairos never asked to sync, and a signal on every row is none.
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

    /// <summary>
    /// The identity is the provider's to mint and Kairos's to keep: the confirmation quotes this
    /// value, and a booking whose row lost it publishes itself a second time.
    /// </summary>
    [Fact]
    public async Task SyncAsync_TheCalendarPlacesTheEvent_KeepsTheUidItWasGiven()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);

        await Service(context, new FakeCalendar(), new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        context.CalendarEntries.AsNoTracking().Single().CalendarUid.ShouldBe(CalendarUid);
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
    /// The whole of gap 1 in one assertion: after a move the calendar holds one event, under the
    /// id it always had, at the time the booking now claims. A second event is a second invitation
    /// to everybody who accepted; the same event left behind is the invitee an hour early.
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
    /// never written to a calendar is not placed into one by a reschedule: that would put a
    /// meeting in front of the organizer the booker was never told was there.
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

    /// <summary>
    /// The switch, on the one path that had no check for it. A provider paused by hand is not a
    /// provider refusing, and recording it as one wrote <c>Failed</c> with the adapter's "no
    /// account is connected" onto every meeting rescheduled while the page said Paused. The
    /// refusal is set here because that is what a real paused adapter does when it is reached at
    /// all — the point of the guard is that it never is.
    /// </summary>
    [Fact]
    public async Task RescheduleAsync_TheProviderIsPaused_RecordsNoFailureAndLeavesTheEventAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        await MoveAsync(context, booking);

        await PauseAsync(context);
        calendar.Failure = new InvalidOperationException(Refusal);

        await service.RescheduleAsync(booking, TestContext.Current.CancellationToken);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Synced);
        entry.LastError.ShouldBeNull();

        // Where the pause left it: still in the calendar, still at the hour it was placed at. A
        // provider that is not being written to cannot have moved anything, and the row must not
        // claim it did either way.
        calendar.Events[EventId].Start.ToUnixTimeSeconds().ShouldBe(MeetingStartUtc);
    }

    /// <summary>
    /// The same switch on the withdrawal, where the untrue row would have been worse: a
    /// cancellation is the last thing that happens to a booking, so the failure it recorded would
    /// have been the permanent state of that meeting.
    /// </summary>
    [Fact]
    public async Task CancelAsync_TheProviderIsPaused_RecordsNoFailureAndNeverReachesTheProvider()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());

        await service.SyncAsync(booking, TestContext.Current.CancellationToken);

        await PauseAsync(context);
        calendar.Failure = new InvalidOperationException(Refusal);

        await service.CancelAsync(booking.Id, TestContext.Current.CancellationToken);

        var entry = context.CalendarEntries.AsNoTracking().Single();
        entry.State.ShouldBe(CalendarSyncState.Synced);
        entry.LastError.ShouldBeNull();
        calendar.Deletes.ShouldBe(NeverReached);
    }

    private const int NeverReached = 0;

    private const int ReachedOnce = 1;

    /// <summary>
    /// The retry trap. A follow-up whose mail step failed is retried whole, so this method runs a
    /// second time against a booking that is already in the calendar. Without the guard the second
    /// insert comes back a duplicate, RecordFailureAsync catches it, and a row that is telling the
    /// truth is overwritten with Failed — a mail outage corrupting calendar state.
    /// </summary>
    [Fact]
    public async Task SyncAsync_RunTwice_ReachesTheProviderOnceAndLeavesTheEntrySynced()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var conference = new StubConference();

        await Service(context, calendar, conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);
        await Service(context, calendar, conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Creates.ShouldBe(ReachedOnce);
        conference.Creates.ShouldBe(ReachedOnce);
        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
        context.ConferenceLinks.AsNoTracking().Count().ShouldBe(ReachedOnce);
    }

    /// <summary>
    /// The same trap on the cancel path, with a different provider error. A retried delete of an
    /// already-deleted event answers Gone, and without the guard that answer overwrites a
    /// Cancelled row with Failed.
    /// </summary>
    [Fact]
    public async Task CancelAsync_RunTwice_ReachesTheProviderOnceAndLeavesTheEntryCancelled()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();

        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);
        await Service(context, calendar, new StubConference())
            .CancelAsync(booking.Id, TestContext.Current.CancellationToken);
        await Service(context, calendar, new StubConference())
            .CancelAsync(booking.Id, TestContext.Current.CancellationToken);

        calendar.Deletes.ShouldBe(ReachedOnce);
        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Cancelled);
    }

    /// <summary>
    /// The guard must not swallow a real retry. A first attempt the provider refused leaves a
    /// Failed row, and the follow-up's next tick has to try again — a guard keyed on "a row
    /// exists" rather than on "a row says Synced" would strand every booking made during an
    /// outage.
    /// </summary>
    [Fact]
    public async Task SyncAsync_AfterAFailedAttempt_TriesAgainAndSucceeds()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };

        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);
        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Failed);

        calendar.Failure = null;
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
    }

    /// <summary>
    /// The proof the Synced guard did not over-correct: it must not just skip the second call, it
    /// must still finish the conference the first call left undone. Without the guard calling
    /// AddConferenceAsync on this path, a booking whose event synced but whose conference refused
    /// once would never get a join link — no later sync ever reaches PlaceAsync's own provider
    /// call again once the entry is Synced.
    /// </summary>
    [Fact]
    public async Task SyncAsync_TheConferenceRefusedOnce_GetsItsJoinLinkOnASecondSync()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var conference = new RefusingOnceConference();

        await Service(context, calendar, conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);
        context.ConferenceLinks.AsNoTracking().ShouldBeEmpty();

        await Service(context, calendar, conference)
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Creates.ShouldBe(ReachedOnce);
        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
        context.ConferenceLinks.AsNoTracking().Single().JoinUrl.ShouldBe(JoinUrl);
    }

    /// <summary>
    /// The incident: a dead grant refused the event, the grant now works, and the meeting gets its
    /// event and join link without anybody rebooking it.
    /// </summary>
    [Fact]
    public async Task ResyncAsync_AMeetingTheCalendarRefused_PlacesItWithAJoinUrl()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(OneMeeting);
        context.CalendarEntries.AsNoTracking().Single().State.ShouldBe(CalendarSyncState.Synced);
        context.ConferenceLinks.AsNoTracking().Single().JoinUrl.ShouldBe(JoinUrl);

        // D1, option A: the invitee already holds this meeting under the booking's own UID.
        context.CalendarEntries.AsNoTracking().Single().CalendarUid.ShouldBeNull();
    }

    /// <summary>
    /// One more refused meeting than a pass takes: the pass stops at the cap. This pins the cap,
    /// not the order: SQLite returns these bookings in start order even without the OrderBy.
    /// </summary>
    [Fact]
    public async Task ResyncAsync_MoreRefusedMeetingsThanOnePass_StopsAtTheCap()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
        var total = CalendarSyncService.MaxResyncPerPass + OneMeeting;
        var link = SeedLink(context);
        var bookings = new List<Booking>();
        for (var index = total; index >= OneMeeting; index--)
        {
            // Descending, so insertion order is the reverse of start order.
            bookings.Add(Seed(context, startUtc: MeetingStartUtc + (index * StartStepSeconds), existingLink: link));
        }

        foreach (var booking in bookings)
        {
            await Service(context, calendar, new StubConference())
                .SyncAsync(booking, TestContext.Current.CancellationToken);
        }

        calendar.Failure = null;
        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(CalendarSyncService.MaxResyncPerPass);
        var stateByStart = context.CalendarEntries.AsNoTracking()
            .Join(context.Bookings.AsNoTracking(), entry => entry.BookingId, booking => booking.Id,
                (entry, booking) => new { booking.StartUtc, entry.State })
            .ToDictionary(row => row.StartUtc, row => row.State);
        stateByStart[MeetingStartUtc + (OneMeeting * StartStepSeconds)].ShouldBe(CalendarSyncState.Synced);
        stateByStart[MeetingStartUtc + (total * StartStepSeconds)].ShouldBe(CalendarSyncState.Failed);
    }

    [Fact]
    public async Task ResyncAsync_TheMeetingAlreadyStarted_LeavesItAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context, startUtc: StartedUtc);
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(NoMeetings);
        calendar.Creates.ShouldBe(ReachedOnce);
    }

    [Fact]
    public async Task ResyncAsync_TheBookingIsCancelled_LeavesItAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context, status: BookingStatus.Cancelled);
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(NoMeetings);
        calendar.Creates.ShouldBe(ReachedOnce);
    }

    /// <summary>
    /// A Failed row that holds an event id is a refused move: the event exists, so placing it
    /// again would put a second one in the calendar.
    /// </summary>
    [Fact]
    public async Task ResyncAsync_AMoveWasRefused_DoesNotPlaceASecondEvent()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar();
        var service = Service(context, calendar, new StubConference());
        await service.SyncAsync(booking, TestContext.Current.CancellationToken);
        await MoveAsync(context, booking);
        calendar.Failure = new InvalidOperationException(Refusal);
        await service.RescheduleAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        var placed = await service.ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(NoMeetings);
        calendar.Creates.ShouldBe(ReachedOnce);
    }

    /// <summary>
    /// No row means no calendar was connected when the booking was made; placing it now is the
    /// decision <see cref="RescheduleAsync_NothingWasEverPlaced_LeavesTheCalendarAlone"/> refuses.
    /// </summary>
    [Fact]
    public async Task ResyncAsync_NothingWasEverAttempted_LeavesTheCalendarAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context);
        var calendar = new FakeCalendar();

        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: null, TestContext.Current.CancellationToken);

        placed.ShouldBe(NoMeetings);
        calendar.Creates.ShouldBe(NeverReached);
    }

    [Fact]
    public async Task ResyncAsync_ScopedToAnotherLink_LeavesThisLinksMeetingAlone()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Seed(context);
        var calendar = new FakeCalendar { Failure = new InvalidOperationException(Refusal) };
        await Service(context, calendar, new StubConference())
            .SyncAsync(booking, TestContext.Current.CancellationToken);

        calendar.Failure = null;
        var placed = await Service(context, calendar, new StubConference())
            .ResyncAsync(linkId: Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        placed.ShouldBe(NoMeetings);
        calendar.Creates.ShouldBe(ReachedOnce);
    }

    private static CalendarSyncService Service(
        KairosDbContext context,
        ICalendarWriter calendars,
        IConferenceCreator conferences) =>
        new(
            calendars,
            conferences,
            new IntegrationCredentialStore(context, new SecretProtector(TestKey()), SystemClock.Instance),
            context,
            new FixedClock(Now),
            NullLogger<CalendarSyncService>.Instance);

    /// <summary>
    /// A key of the right length and nothing more. The store is reached here for the switch alone,
    /// which is a column; no test in this file writes or opens a payload.
    /// </summary>
    private static string TestKey() => Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    /// <summary>
    /// The operator's switch, thrown on a provider that is connected. The payload is never opened
    /// on this path, so the row carries a placeholder rather than ciphertext — what the switch
    /// costs a caller is one column read.
    /// </summary>
    private static Task PauseAsync(KairosDbContext context)
    {
        context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = UnopenedPayload,
            Enabled = false,
            ConnectedAt = MeetingStartUtc,
            UpdatedAt = MeetingStartUtc,
        });

        return context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

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

    private static Link SeedLink(KairosDbContext context)
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

        return link;
    }

    /// <summary>
    /// Returned with its link and participants loaded, because that is the state the create
    /// endpoint hands the service a booking in.
    /// </summary>
    private static Booking Seed(
        KairosDbContext context,
        long startUtc = MeetingStartUtc,
        BookingStatus status = BookingStatus.Confirmed,
        Link? existingLink = null)
    {
        var link = existingLink ?? SeedLink(context);

        var bookingId = Guid.CreateVersion7();
        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            LinkId = link.Id,
            StartUtc = startUtc,
            EndUtc = startUtc + (MeetingEndUtc - MeetingStartUtc),
            Status = status,
            ManageTokenHash = bookingId.ToString(),
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
            .Single(booking => booking.Id == bookingId);
    }

    /// <summary>
    /// A calendar, not a call recorder. It holds the events it is given and hands them back, so
    /// the assertions read what an organizer would see. Asserting <c>UpdateEventAsync</c> was
    /// invoked would pass against a delete-and-recreate, the one thing a move must never do.
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

        /// <summary>
        /// How many times the provider was actually reached. The dictionary cannot answer this —
        /// a second insert under the same id overwrites its entry and looks like one call, which
        /// is the failure these guards exist to prevent.
        /// </summary>
        public int Creates { get; private set; }

        public int Deletes { get; private set; }

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Connected);

        public Task<PlacedEvent> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken)
        {
            Creates++;

            if (Failure is not null)
            {
                return Task.FromException<PlacedEvent>(Failure);
            }

            _events[EventId] = request;
            return Task.FromResult(new PlacedEvent(EventId, CalendarUid));
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
            Deletes++;

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

        public int Creates { get; private set; }

        public Task<ConferenceDetails> CreateAsync(
            Guid bookingId,
            CancellationToken cancellationToken)
        {
            Creates++;

            return Failure is null
                ? Task.FromResult(new ConferenceDetails(ConferenceId, new Uri(JoinUrl)))
                : Task.FromException<ConferenceDetails>(Failure);
        }
    }

    /// <summary>
    /// Refuses its first attempt and takes every one after it, the shape of a conferencing
    /// provider having a bad moment rather than being down.
    /// </summary>
    private sealed class RefusingOnceConference : IConferenceCreator
    {
        private const int FirstAttempt = 1;

        public int Attempts { get; private set; }

        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<ConferenceDetails> CreateAsync(Guid bookingId, CancellationToken cancellationToken)
        {
            Attempts++;

            return Attempts == FirstAttempt
                ? Task.FromException<ConferenceDetails>(new InvalidOperationException(Refusal))
                : Task.FromResult(new ConferenceDetails(ConferenceId, new Uri(JoinUrl)));
        }
    }
}
