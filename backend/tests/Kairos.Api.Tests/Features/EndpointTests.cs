using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Invites;
using Kairos.Api.Features.Slots;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The HTTP surface over the real pipeline. What is pinned is the wiring — which route answers,
/// which failure becomes which status — and the two rules living only at this layer: a booking is
/// re-checked against generated slots before it is written, and a reschedule moves the meeting.
/// </summary>
public class EndpointTests
{
    private const string Slug = "consulting";

    /// <summary>A slug no link can have, so both link routes must answer the same way.</summary>
    private const string UnusableSlug = "ABC_%21";

    /// <summary>
    /// Well-formed and unseeded, so both link routes reach their resolver and refuse it there.
    /// Distinctive enough that finding it anywhere in a response body means it was reflected.
    /// </summary>
    private const string UnknownSlug = "quenelle-of-nothing";

    private const int DurationMinutes = 25;
    private const int LongDurationMinutes = 50;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const string FirstSlotTime = "T09:00:00Z";
    private const string SecondSlotTime = "T09:30:00Z";
    private const string ThirdSlotTime = "T10:00:00Z";
    private const string NeverOfferedTime = "T03:00:00Z";

    /// <summary>Thirty-two lowercase hex, the shape the Google adapter derives from a booking id.</summary>
    private const string ExternalEventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";

    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";

    /// <summary>Days ahead of the seeding, so real-clock minimum notice never decides a test.</summary>
    private const int WellAhead = 30;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    [Fact]
    public async Task GetSlots_UnderTheVersionPrefix_ReturnsTheDaysInstants()
    {
        using var app = NewApp();
        var day = Day();

        var slots = await app.Client.GetFromJsonAsync<string[]>(
            SlotsUrl(day, day), TestContext.Current.CancellationToken);

        slots.ShouldNotBeNull();
        slots.ShouldContain(day + FirstSlotTime);
        slots.ShouldNotContain(day + NeverOfferedTime);
    }

    /// <summary>
    /// 30 February parses as a date shape and is not a date. That is why the parse, not a regular
    /// expression, is what decides — and why it has to answer 400 rather than fall through to a
    /// 500 from somewhere further down.
    /// </summary>
    [Fact]
    public async Task GetSlots_MalformedDate_IsABadRequestNamingTheCode()
    {
        using var app = NewApp();

        var response = await app.Client.GetAsync(
            SlotsUrl("2026-02-30", "2026-02-30"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(SlotsEndpoints.DateInvalidCode);
    }

    /// <summary>
    /// An inverted range answers empty inside SlotQuery, which the booking page would render as
    /// "no times available" — a real answer, for a request that is not one.
    /// </summary>
    [Fact]
    public async Task GetSlots_ToBeforeFrom_IsABadRequestRatherThanAnEmptyList()
    {
        using var app = NewApp();
        var day = Day();

        var response = await app.Client.GetAsync(
            SlotsUrl(day, LocalDatePattern.Iso.Format(LocalDatePattern.Iso.Parse(day).Value.PlusDays(-1))),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOfAsync(response)).ShouldBe(SlotsEndpoints.RangeInvertedCode);
    }

    /// <summary>
    /// The other half of the handler's contract, end to end: a stored zone nobody can resolve is
    /// a bug, and a bug stays a 500 with a stack trace instead of being dressed up as a 400.
    /// </summary>
    [Fact]
    public async Task GetSlots_UnexpectedFailure_StaysAServerError()
    {
        using var app = NewApp();
        app.Seed(context => context.Settings
            .Where(row => row.Id == Settings.SingletonId)
            .ExecuteUpdate(row => row.SetProperty(settings => settings.TimeZoneId, "Mars/Olympus")));

        var day = Day();
        var response = await app.Client.GetAsync(SlotsUrl(day, day), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// The wiring an invite's own tests cannot reach: <c>InviteToken</c> has to arrive at the
    /// writer's last argument, and the refusal it answers with has to become a 409 carrying the
    /// code a page branches on. The second booking asks for a slot nothing has taken, so the
    /// invite is the only thing that can explain the conflict.
    /// </summary>
    [Fact]
    public async Task CreateBooking_TheSameInviteTwice_BooksOnceAndThenConflicts()
    {
        using var app = NewApp();
        var day = Day();
        var token = SeedInvite(app);

        var first = await CreateAsync(app, day + FirstSlotTime, token);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var booked = BookingIdOf(await ReadJsonAsync(first));

        var second = await CreateAsync(app, day + ThirdSlotTime, token);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(second)).ShouldBe(BookingWriter.InviteSpentReason);
        app.Read(context => context.Bookings.Count()).ShouldBe(1);
        app.Read(context => context.PersonalInvites.AsNoTracking().Single().BookingId).ShouldBe(booked);
    }

    /// <summary>
    /// An unfilled form field arrives as <c>""</c>, which is how <c>Notes</c> already reaches this
    /// endpoint. Empty has to mean no invite rather than a token that matches nothing, or a page
    /// that renders the invite field for everyone turns every ordinary booking into a 409.
    /// </summary>
    [Fact]
    public async Task CreateBooking_AnEmptyInviteToken_IsAnOrdinaryBooking()
    {
        using var app = NewApp();
        SeedInvite(app);

        var response = await CreateAsync(app, Day() + FirstSlotTime, string.Empty);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        app.Read(context => context.Bookings.Count()).ShouldBe(1);
        app.Read(context => context.PersonalInvites.AsNoTracking().Single().ConsumedAt).ShouldBeNull();
    }

    /// <summary>
    /// The writer's SQL only refuses a clash with another confirmed booking, so 03:00 on a day
    /// that opens at 09:00 would be written without complaint. Availability, notice and cap are
    /// enforced on write because this endpoint asks for the slots again first.
    /// </summary>
    [Fact]
    public async Task CreateBooking_InstantTheLinkNeverOffered_IsRefusedBeforeTheWriter()
    {
        using var app = NewApp();

        var response = await CreateAsync(app, Day() + NeverOfferedTime);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(BookingsEndpoints.SlotUnavailableCode);
        app.Read(context => context.Bookings.Count()).ShouldBe(0);
    }

    [Fact]
    public async Task RescheduleBooking_MovesTheMeeting_KeepsTheIdentifierAndIncrementsSequence()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var moved = await ReadJsonAsync(response);
        moved.GetProperty("id").GetString().ShouldBe(created.GetProperty("id").GetString());
        moved.GetProperty("start").GetString().ShouldBe(day + ThirdSlotTime);
        moved.GetProperty("sequence").GetInt32()
            .ShouldBe(created.GetProperty("sequence").GetInt32() + 1);
        app.Read(context => context.Bookings.Count()).ShouldBe(1);
    }

    /// <summary>
    /// A booking does not block its own move. A confirmed meeting pads its slot by the margin, so
    /// the slot directly after it is covered by the meeting being moved — which is what the
    /// release before the re-check exists to clear.
    /// </summary>
    /// <remarks>
    /// The gap was invisible: every other reschedule here moves to an hour the padded block never
    /// reached, so deleting the release left the suite green. Found by mutating it while building
    /// the operator's propose, which had the same hole.
    /// </remarks>
    [Fact]
    public async Task RescheduleBooking_TheSlotDirectlyAfterIt_IsNotBlockedByTheMeetingBeingMoved()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + SecondSlotTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("start").GetString().ShouldBe(day + SecondSlotTime);
    }

    [Fact]
    public async Task CancelBooking_TokenThatMatchesNothing_IsNotFound()
    {
        using var app = NewApp();
        await CreateAsync(app, Day() + FirstSlotTime);

        var response = await app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/not-a-token/cancel", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Read(context => context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed))
            .ShouldBe(1);
    }

    /// <summary>
    /// The branch the release-then-re-check design exists to justify. A reschedule that fails must
    /// undo the release it made to run the check; otherwise the caller sees an error and believes
    /// their meeting stands while the row sits cancelled. Invisible to a status-code assertion.
    /// </summary>
    [Fact]
    public async Task RescheduleBooking_InstantTheLinkNeverOffered_LeavesTheRowConfirmedWhereItWas()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + NeverOfferedTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe(BookingsEndpoints.SlotUnavailableCode);

        var row = app.Read(context => context.Bookings.AsNoTracking().Single());
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        row.Sequence.ShouldBe(created.GetProperty("sequence").GetInt32());
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(day + FirstSlotTime);
    }

    /// <summary>
    /// One window and no other: the invitee's connection dropping between the release the
    /// reschedule commits and the restore that puts the row back. What it pins is that the
    /// compensating write outlives the request — everything that would withdraw the calendar
    /// event and announce the cancellation lives in the cancel endpoint and never runs here.
    /// </summary>
    /// <remarks>
    /// The abort fires from a busy source, the last thing the re-check touches before it reads
    /// the database again, which puts the dead token exactly at the restore. A client-side timer
    /// would land wherever the machine happened to be.
    /// </remarks>
    [Fact]
    public async Task RescheduleBooking_ConnectionDropsDuringTheReCheck_StillPutsTheRowBackConfirmed()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        using var dropped = new CancellationTokenSource();
        using var client = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(
                services => services.AddScoped<IBusySource>(_ => new DroppingBusySource(dropped))))
            .CreateClient();

        await Should.ThrowAsync<OperationCanceledException>(async () => await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            dropped.Token));

        var row = app.Read(context => context.Bookings.AsNoTracking().Single());
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(day + FirstSlotTime);
    }

    /// <summary>
    /// The second of the two 400 shapes, and the only thing that would notice AddValidation
    /// quietly ceasing to run: nothing below this layer looks at an address, so an unvalidated
    /// one is written to the database and answered 201.
    /// </summary>
    [Fact]
    public async Task CreateBooking_ViolatesAnAttribute_IsABadRequestWithErrorsAndNoCode()
    {
        using var app = NewApp();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start = Day() + FirstSlotTime,
                durationMinutes = DurationMinutes,
                name = "Vlad",
                email = "not-an-address",
                timeZone = "Europe/Warsaw",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await ReadJsonAsync(response);
        body.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.TryGetProperty(nameof(CreateBookingRequest.Email), out _).ShouldBeTrue();
        body.TryGetProperty("code", out _).ShouldBeFalse();
        app.Read(context => context.Bookings.Count()).ShouldBe(0);
    }

    /// <summary>
    /// What the manage page loads, and the one guarantee the whole token scheme rests on: the
    /// plaintext is handed over exactly once, by create. Only its hash is stored, so a read that
    /// returned it again would mean it had been kept somewhere it should not be.
    /// </summary>
    [Fact]
    public async Task GetBooking_ValidToken_ReturnsTheBookingAndNeverTheTokenAgain()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.GetAsync(
            $"{ApiVersions.V1}/bookings/{token}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await ReadJsonAsync(response);
        read.GetProperty("id").GetString().ShouldBe(created.GetProperty("id").GetString());
        read.GetProperty("start").GetString().ShouldBe(day + FirstSlotTime);
        read.GetProperty("manageToken").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// The booking page reads both of these off the link: the durations in the order the
    /// organizer chose, and the zone every instant in this API is derived from.
    /// </summary>
    [Fact]
    public async Task GetLink_ReturnsTheOrganizersZoneAndTheDurationsInTheirOwnOrder()
    {
        using var app = NewApp();

        var link = await app.Client.GetFromJsonAsync<JsonElement>(
            $"{ApiVersions.V1}/links/{Slug}", TestContext.Current.CancellationToken);

        link.GetProperty("timeZone").GetString().ShouldBe(SchedulingDefaults.TimeZoneId);
        link.GetProperty("durations").EnumerateArray().Select(duration => duration.GetInt32()).ToArray()
            .ShouldBe([LongDurationMinutes, DurationMinutes]);
    }

    /// <summary>
    /// Both link routes take the same slug and must answer it the same way. They did not: a
    /// pattern attribute on the slots request made an unusable slug a 400 there and a 404 here.
    /// The resolver's answer is the right one, so the attribute went.
    /// </summary>
    [Fact]
    public async Task UnusableSlug_IsNotFoundOnBothLinkRoutes()
    {
        using var app = NewApp();
        var day = Day();

        var detail = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnusableSlug}", TestContext.Current.CancellationToken);
        var slots = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnusableSlug}/slots?from={day}&to={day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        slots.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOfAsync(detail)).ShouldBe(DomainException.NotFoundCode);
        (await CodeOfAsync(slots)).ShouldBe(DomainException.NotFoundCode);
    }

    /// <summary>
    /// One condition, one body. A slug nothing answers to is the same fact on both routes, so the
    /// sentence a client renders has to be the same one — and may not be built from what the
    /// caller sent. The slots read reflected it; the link read did not.
    /// </summary>
    [Fact]
    public async Task UnknownSlug_IsNotFoundOnBothLinkRoutes_WithOneTitleAndNoEchoOfTheSlug()
    {
        using var app = NewApp();
        var day = Day();

        var detail = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnknownSlug}", TestContext.Current.CancellationToken);
        var slots = await app.Client.GetAsync(
            $"{ApiVersions.V1}/links/{UnknownSlug}/slots?from={day}&to={day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        slots.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TitleOfAsync(slots)).ShouldBe(await TitleOfAsync(detail));

        // The whole body, not just the title: a slug that reached any other field would be the
        // same reflection wearing a different key.
        (await BodyOfAsync(detail)).ShouldNotContain(UnknownSlug);
        (await BodyOfAsync(slots)).ShouldNotContain(UnknownSlug);
    }

    /// <summary>
    /// The wiring the unit tests cannot see: that the contention handler is registered, and that
    /// a write losing the lock for the whole busy timeout reaches it rather than answering 500.
    /// Costs the busy timeout in wall clock, there being no way to hold the lock for less.
    /// </summary>
    [Fact]
    public async Task CreateBooking_AnotherWriterHoldsTheLock_IsServiceUnavailableRatherThanAFault()
    {
        using var app = NewApp();
        using var blocker = new SqliteConnection(app.ConnectionString);

        blocker.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var response = await CreateAsync(app, Day() + FirstSlotTime);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await CodeOfAsync(response)).ShouldBe(DatabaseBusyExceptionHandler.DatabaseBusyCode);
        app.Read(context => context.Bookings.Count()).ShouldBe(0);
    }

    /// <summary>
    /// The state every fresh Kairos is in: no Google client, nothing connected. The booking is
    /// written and nothing is recorded about a calendar — not even a failure row, nothing having
    /// been tried. The announcement goes regardless; a subscriber's interest is unrelated.
    /// </summary>
    [Fact]
    public async Task CreateBooking_NoCalendarProviderConnected_StillSucceedsAndRecordsNoSyncFailure()
    {
        using var app = NewApp();

        var response = await CreateAsync(app, Day() + FirstSlotTime);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        app.Read(context => context.Bookings.Count()).ShouldBe(1);
        app.Read(context => context.CalendarEntries.Count()).ShouldBe(0);
        app.Read(context => context.ConferenceLinks.Count()).ShouldBe(0);
        app.Read(context => context.OutboxMessages.Count()).ShouldBe(1);

        // And the read says so rather than guessing. Absent is not "failed": a UI that rendered
        // the two the same would put a red mark on every booking of every Kairos that was never
        // asked to sync, which is the whole reason nothing is written above.
        var created = await ReadJsonAsync(response);
        created.GetProperty("calendarSync").ValueKind.ShouldBe(JsonValueKind.Null);
        created.GetProperty("conferenceUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Gap 3's contract, in the shape Part C's Meetings tab consumes: the sync state as a word
    /// and the join URL beside it, so a page can say "this meeting is not in your calendar" and
    /// offer the second channel without a second request. The provider's own error text is
    /// deliberately absent — unfiltered third-party prose on a response a manage token can read.
    /// </summary>
    [Fact]
    public async Task GetBooking_SyncedWithAConference_ReportsBothOnTheRead()
    {
        using var app = NewApp();

        var created = await ReadJsonAsync(await CreateAsync(app, Day() + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();
        SeedCalendar(app, BookingIdOf(created), CalendarSyncState.Synced, withConference: true);

        var booking = await ReadJsonAsync(await app.Client.GetAsync(
            $"{ApiVersions.V1}/bookings/{token}", TestContext.Current.CancellationToken));

        booking.GetProperty("calendarSync").GetString().ShouldBe("synced");
        booking.GetProperty("conferenceUrl").GetString().ShouldBe(JoinUrl);
    }

    /// <summary>
    /// Gap 1 at the layer that had it. Both calendar call sites were in create, so a meeting that
    /// moved left its event where it was. The harness connects no Google, so reaching the calendar
    /// shows as the entry going <c>failed</c>, where never calling leaves it <c>synced</c>.
    /// </summary>
    [Fact]
    public async Task RescheduleBooking_MeetingAlreadyInACalendar_ReachesTheCalendarToMoveIt()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();
        SeedCalendar(app, BookingIdOf(created), CalendarSyncState.Synced, withConference: false);

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("calendarSync").GetString().ShouldBe("failed");

        // Still there, because it is the only handle for fixing the stale event by hand.
        app.Read(context => context.CalendarEntries.AsNoTracking().Single().ExternalEventId)
            .ShouldBe(ExternalEventId);
    }

    /// <summary>
    /// Gap 2, same argument. A cancellation that never reached the calendar left the meeting in
    /// the organizer's day for an appointment nobody was coming to.
    /// </summary>
    [Fact]
    public async Task CancelBooking_MeetingAlreadyInACalendar_ReachesTheCalendarToWithdrawIt()
    {
        using var app = NewApp();

        var created = await ReadJsonAsync(await CreateAsync(app, Day() + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();
        SeedCalendar(app, BookingIdOf(created), CalendarSyncState.Synced, withConference: false);

        var response = await app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/{token}/cancel", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("calendarSync").GetString().ShouldBe("failed");
    }

    /// <summary>
    /// The same state one integration over: no relay configured. The mailer runs after the write
    /// has committed, so a relay that threw would turn every booking into a 500 for a reason no
    /// booker could act on. The harness sets no Email keys, which is the case pinned.
    /// </summary>
    [Fact]
    public async Task CreateBooking_NoRelayConfigured_StillWritesTheBookingAndAnswers201()
    {
        using var app = NewApp();

        var response = await CreateAsync(app, Day() + FirstSlotTime);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadJsonAsync(response)).GetProperty("manageToken").GetString().ShouldNotBeNullOrEmpty();
        app.Read(context => context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed))
            .ShouldBe(1);
    }

    /// <summary>
    /// The cancellation the subscriber is owed, in the same transaction as the state change that
    /// caused it. Announced from the endpoint rather than from wherever the status is written,
    /// which the next test is the reason for.
    /// </summary>
    [Fact]
    public async Task CancelBooking_ConfirmedBooking_QueuesTheCancellationBesideTheCreation()
    {
        using var app = NewApp();

        var created = await ReadJsonAsync(await CreateAsync(app, Day() + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/{token}/cancel", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        QueuedTypes(app).ShouldBe([OutboxMessageType.BookingCreated, OutboxMessageType.BookingCancelled]);
    }

    /// <summary>
    /// The trap in wiring <c>booking.cancelled</c> at all. A reschedule sets the booking cancelled
    /// and back to confirmed on its way through, so an announcement hung off the status change
    /// would call a moved meeting off — and leave the real cancellation unannounceable.
    /// </summary>
    [Fact]
    public async Task RescheduleBooking_ReleasesTheSlotToRePlaceIt_AnnouncesNoCancellation()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var response = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        QueuedTypes(app).ShouldBe([OutboxMessageType.BookingCreated]);
    }

    /// <summary>
    /// A double-clicked cancel button. The endpoint was idempotent before it enqueued anything
    /// and has to stay so: a second cancellation is one the unique index refuses, and refusing it
    /// must not turn a 200 into a 500.
    /// </summary>
    [Fact]
    public async Task CancelBooking_Twice_AnswersTheSameAndQueuesOneCancellation()
    {
        using var app = NewApp();

        var created = await ReadJsonAsync(await CreateAsync(app, Day() + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();
        var url = $"{ApiVersions.V1}/bookings/{token}/cancel";

        await app.Client.PostAsync(url, null, TestContext.Current.CancellationToken);
        var again = await app.Client.PostAsync(url, null, TestContext.Current.CancellationToken);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(again)).GetProperty("status").GetString().ShouldBe("cancelled");
        QueuedTypes(app).ShouldBe([OutboxMessageType.BookingCreated, OutboxMessageType.BookingCancelled]);
    }

    /// <summary>
    /// The invariant the whole .ics side rests on: <c>Booking.Sequence</c> is the SEQUENCE of the
    /// last iCalendar message composed for this booking, so it only ever goes up. A client keeps
    /// the highest it has seen and silently drops anything that does not outrank it, so a
    /// cancellation reusing the reschedule's number leaves the meeting in the invitee's calendar
    /// — which is why RFC 5546 requires the CANCEL to outrank the REQUEST it withdraws.
    /// </summary>
    [Fact]
    public async Task Booking_CreatedThenMovedThenCancelled_AdvancesTheSequenceEveryTime()
    {
        using var app = NewApp();
        var day = Day();

        var created = await ReadJsonAsync(await CreateAsync(app, day + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();

        var moved = await ReadJsonAsync(await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{token}/reschedule",
            new { start = day + ThirdSlotTime },
            TestContext.Current.CancellationToken));

        var cancelled = await ReadJsonAsync(await app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/{token}/cancel", null, TestContext.Current.CancellationToken));

        created.GetProperty("sequence").GetInt32().ShouldBe(0);
        moved.GetProperty("sequence").GetInt32().ShouldBe(1);
        cancelled.GetProperty("sequence").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// The other half of the same rule, and the reason the advance sits behind the idempotency
    /// guard rather than in front of it. A double-clicked cancel button must not walk the sequence
    /// twice — there is one cancellation, so there is one message and one number for it.
    /// </summary>
    [Fact]
    public async Task CancelBooking_Twice_AdvancesTheSequenceOnce()
    {
        using var app = NewApp();

        var created = await ReadJsonAsync(await CreateAsync(app, Day() + FirstSlotTime));
        var token = created.GetProperty("manageToken").GetString();
        var url = $"{ApiVersions.V1}/bookings/{token}/cancel";

        await app.Client.PostAsync(url, null, TestContext.Current.CancellationToken);
        var again = await app.Client.PostAsync(url, null, TestContext.Current.CancellationToken);

        (await ReadJsonAsync(again)).GetProperty("sequence").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// A busy source that reports nothing and hangs up on the way out. It is registered for one
    /// client only, so every other request in this file meets exactly the sources the composition
    /// root registers.
    /// </summary>
    /// <remarks>Qualified: NodaTime has an Interval too, and both namespaces are in scope here.</remarks>
    private sealed class DroppingBusySource(CancellationTokenSource dropped) : IBusySource
    {
        public Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
            Kairos.Api.Scheduling.Interval window,
            CancellationToken cancellationToken)
        {
            dropped.Cancel();
            return Task.FromResult<IReadOnlyList<BusyBlock>>([]);
        }
    }

    /// <summary>Oldest first, which is also the order the dispatcher delivers them in.</summary>
    private static List<OutboxMessageType> QueuedTypes(KairosApp app) =>
        app.Read(context => context.OutboxMessages
            .AsNoTracking()
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message => message.Type)
            .ToList());

    /// <summary>
    /// A booking that is already in somebody's calendar, which is the state the harness cannot
    /// reach on its own: no Google is connected, so nothing writes these rows for it.
    /// </summary>
    private static void SeedCalendar(
        KairosApp app,
        Guid bookingId,
        CalendarSyncState state,
        bool withConference) =>
        app.Seed(context =>
        {
            context.CalendarEntries.Add(new CalendarEntry
            {
                BookingId = bookingId,
                Provider = IntegrationProvider.Google,
                ExternalEventId = ExternalEventId,
                State = state,
            });

            if (!withConference)
            {
                return;
            }

            context.ConferenceLinks.Add(new ConferenceLink
            {
                BookingId = bookingId,
                Provider = IntegrationProvider.Google,
                ExternalId = "abc-defg-hij",
                JoinUrl = JoinUrl,
            });
        });

    private static Guid BookingIdOf(JsonElement booking) =>
        booking.GetProperty("id").GetGuid();

    private static KairosApp NewApp()
    {
        var app = new KairosApp();
        app.Seed(Seed);
        return app;
    }

    private static void Seed(KairosDbContext context)
    {
        context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
        context.PatternRules.Add(new PatternRule
        {
            Id = Guid.CreateVersion7(),
            PatternId = PatternId,
            IsoDayOfWeek = (int)IsoDayOfWeek.Monday,
            StartMinute = NineAm,
            EndMinute = FivePm,
        });
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = "Consulting call",
            PatternId = PatternId,
            Active = true,
        });
        // Listed longest first, so sort order and ascending length disagree. A booking page that
        // reorders them is showing the organizer a list they did not choose.
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = LongDurationMinutes, SortOrder = 0 });
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = DurationMinutes, SortOrder = 1 });
    }

    /// <summary>
    /// Computed rather than written down. The application reads the real clock, so a literal date
    /// would turn these tests into a suite that passes until it silently stops.
    /// </summary>
    private static string Day() => LocalDatePattern.Iso.Format(
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday));

    private static string SlotsUrl(string from, string to) =>
        $"{ApiVersions.V1}/links/{Slug}/slots?from={from}&to={to}&duration={DurationMinutes}";

    /// <param name="inviteToken">
    /// Sent on every call, null where the case is not about invites: a payload naming the field
    /// and leaving it null is what an ordinary booking from the real page looks like.
    /// </param>
    private static Task<HttpResponseMessage> CreateAsync(
        KairosApp app, string start, string? inviteToken = null) =>
        app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start,
                durationMinutes = DurationMinutes,
                name = "Vlad",
                email = "vlad@example.com",
                timeZone = "Europe/Warsaw",
                inviteToken,
            },
            TestContext.Current.CancellationToken);

    /// <summary>
    /// One open invite on the seeded link, the token minted beside it so the stored hash is the
    /// one the write will look that token up by — the shape <see cref="InviteEndpointTests"/>
    /// seeds. Nothing on this path reads the prefill or the creation stamp.
    /// </summary>
    private static string SeedInvite(KairosApp app)
    {
        var (token, hash) = InviteWriter.NewToken();

        app.Seed(context => context.PersonalInvites.Add(new PersonalInvite
        {
            Id = Guid.CreateVersion7(),
            LinkId = LinkId,
            TokenHash = hash,
            Prefill = InvitePrefill.Empty,
            CreatedAt = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds(),
        }));

        return token;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString();

    private static async Task<string?> TitleOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("title").GetString();

    private static Task<string> BodyOfAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
