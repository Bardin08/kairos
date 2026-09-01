using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// What happens to a scheduled reminder when the meeting underneath it changes.
/// </summary>
/// <remarks>
/// The two paths are already single — <see cref="BookingMover.MoveToAsync"/> is the only place
/// StartUtc changes, and <see cref="BookingCanceller"/> the only place a booking stays cancelled —
/// which is what makes a materialized due moment affordable. These tests are what hold that in
/// place.
///
/// The booking under each case is written by <see cref="BookingWriter"/> rather than seeded as
/// rows, so the reminder being maintained here is the one the application actually writes. The two
/// reschedule cases go further and drive the endpoint: the transit through
/// <see cref="BookingStatus.Cancelled"/> they exist to watch happens between
/// <see cref="BookingMover.ReleaseAndCheckAsync"/> and the move, so calling the mover alone cannot
/// see it.
/// </remarks>
public class ReminderMaintenanceTests
{
    private const int NoMargin = 0;
    private const int MeetingMinutes = 30;

    /// <summary>
    /// Far enough out that the seeded booking is always given a reminder, and far enough from
    /// every other instant here that the two due moments cannot be confused.
    /// </summary>
    private const int AWeekAhead = 7;

    private const int ADayLater = 1;

    /// <summary>
    /// Where "moved inside the lead" is placed. Any distance under
    /// <see cref="ReminderSchedule.Lead"/> would do; the boundary itself belongs to
    /// <see cref="ReminderScheduleTests"/>.
    /// </summary>
    private const int InsideTheLeadHours = 2;

    /// <summary>How far back out the third case drags the meeting, past where it began.</summary>
    private const int TwoWeeksAhead = 14;

    /// <summary>A reminder the relay accepted first time, which is every reminder that goes out.</summary>
    private const int OneAttempt = 1;

    /// <summary>
    /// Days ahead of the seeding for the endpoint-driven cases, which run on the real clock: far
    /// enough that minimum notice never decides them, and far past
    /// <see cref="ReminderSchedule.Lead"/> at both ends of the move.
    /// </summary>
    private const int WellAhead = 30;

    /// <summary>The link offers one length, so its place in the operator's ordering is the only one.</summary>
    private const int OnlyDurationOrder = 0;

    private const int NineAmMinute = 9 * 60;
    private const int FivePmMinute = 17 * 60;
    private const string FirstSlotTime = "T09:00:00Z";
    private const string LaterSlotTime = "T10:00:00Z";

    /// <summary>Before the pattern opens, so the re-check inside the release refuses it.</summary>
    private const string NeverOfferedTime = "T03:00:00Z";

    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string PatternName = "Weekdays";

    private static readonly Guid LinkId = Guid.CreateVersion7();

    /// <summary>Fixed, because every case here is a statement about which side of the lead a start falls on.</summary>
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 7, 9, 0);

    private static readonly Instant OriginalStart = Now + Duration.FromDays(AWeekAhead);
    private static readonly Instant LaterStart = OriginalStart + Duration.FromDays(ADayLater);
    private static readonly Instant InsideTheLeadStart = Now + Duration.FromHours(InsideTheLeadHours);
    private static readonly Instant FinalStart = Now + Duration.FromDays(TwoWeeksAhead);
    private static readonly Duration Meeting = Duration.FromMinutes(MeetingMinutes);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    /// <summary>
    /// Thirty-two zero bytes, base64. A key a test can name, and one no deployment would ever
    /// hold — <see cref="SecretProtector"/> only checks the length.
    /// </summary>
    private static readonly string TestKey = Convert.ToBase64String(new byte[SecretProtector.KeyBytes]);

    /// <remarks>
    /// The surviving row is a different row. Delete-then-write is a decision rather than an
    /// implementation detail — a reminder already sent must not be carried over onto a new time —
    /// and <see cref="Uuid7ValueGenerator"/> mints a key only for an entity being added, so the
    /// identifier is what tells a write apart from an update. Without this the whole case passes
    /// against an in-place assignment to <c>DueAt</c>.
    /// </remarks>
    [Fact]
    public async Task MoveTo_ALaterTime_MovesTheReminderWithIt()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);
        var scheduled = context.Reminders.AsNoTracking().Single().Id;

        await Mover(context).MoveToAsync(booking, LaterStart, Now);

        var reminder = context.Reminders.AsNoTracking().Single();
        reminder.BookingId.ShouldBe(booking.Id);
        reminder.DueAt.ShouldBe(ReminderSchedule.DueAt(LaterStart).ToUnixTimeSeconds());
        reminder.Id.ShouldNotBe(scheduled);
    }

    /// <summary>
    /// Moved inside the lead. The reschedule mail the invitee is about to receive IS the notice,
    /// and a reminder firing on the next tick would arrive a minute behind it saying "tomorrow"
    /// about a meeting today.
    /// </summary>
    [Fact]
    public async Task MoveTo_InsideTheLead_DeletesTheReminder()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);

        await Mover(context).MoveToAsync(booking, InsideTheLeadStart, Now);

        context.Reminders.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// And back out again. A meeting dragged into this evening and then out to next week has a
    /// reminder again — the row was deleted, so this is a write rather than a revival, which the
    /// identifier is what proves.
    /// </summary>
    [Fact]
    public async Task MoveTo_BackOutsideTheLead_SchedulesOneAgain()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);
        var mover = Mover(context);

        await mover.MoveToAsync(booking, InsideTheLeadStart, Now);
        await mover.MoveToAsync(booking, FinalStart, Now);

        context.Reminders.AsNoTracking().Single().DueAt
            .ShouldBe(ReminderSchedule.DueAt(FinalStart).ToUnixTimeSeconds());
    }

    /// <summary>
    /// The reminder went out, and only then did the invitee move the meeting. They are reminded
    /// again, about the new time.
    /// </summary>
    /// <remarks>
    /// This is what delete-then-write buys. Carried over in place, the row would keep the
    /// <see cref="Reminder.SentAt"/> that took it out of the sweeper's reach, so the one invitee
    /// who most needs telling — the one already told the wrong hour — is the one nobody tells.
    /// </remarks>
    [Fact]
    public async Task MoveTo_AfterItsReminderWentOut_RemindsAgainAboutTheNewTime()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);
        var wentOutAt = ReminderSchedule.DueAt(OriginalStart);
        var sent = context.Reminders.Single();
        sent.SentAt = wentOutAt.ToUnixTimeSeconds();
        sent.Attempts = OneAttempt;
        context.SaveChanges();

        await Mover(context).MoveToAsync(booking, FinalStart, wentOutAt);

        var reminder = context.Reminders.AsNoTracking().Single();
        reminder.SentAt.ShouldBeNull();
        reminder.DueAt.ShouldBe(ReminderSchedule.DueAt(FinalStart).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Cancel_ABooking_DeletesItsReminder()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var booking = Book(context, OriginalStart);

        await Canceller(context)
            .CancelAsync(booking, reason: null, manageToken: null, CancellationToken.None);

        context.Reminders.AsNoTracking().ShouldBeEmpty();
    }

    /// <summary>
    /// The test this task exists for.
    /// </summary>
    /// <remarks>
    /// Reschedule is release, re-check, re-place: it sets the booking Cancelled and back to
    /// Confirmed on its way through, which <see cref="BookingCanceller"/>'s own remark says of the
    /// outbox. A reminder hooked to the status write rather than to
    /// <see cref="BookingCanceller.CancelAsync"/> deletes this row, and every other assertion in
    /// this suite still passes — including the one directly above.
    ///
    /// Driven through the endpoint, not through <see cref="BookingMover.MoveToAsync"/>: the move
    /// alone never sets the status, and it is the transit that is under test.
    /// </remarks>
    [Fact]
    public async Task Reschedule_WhichPassesThroughCancelled_KeepsTheReminder()
    {
        using var app = NewApp();
        var day = Day();
        var token = await ManageTokenOfAsync(await CreateAsync(app, day + FirstSlotTime));

        var response = await RescheduleAsync(app, token, day + LaterSlotTime);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Reminders.AsNoTracking().Single()).DueAt.ShouldBe(
            ReminderSchedule.DueAt(BookingMover.ParseInstant(day + LaterSlotTime)).ToUnixTimeSeconds());
    }

    /// <summary>
    /// The half of the transit nothing puts back. A refused reschedule releases the booking, is
    /// told no, and restores it — <see cref="BookingMover.MoveToAsync"/> never runs, so nothing
    /// rewrites a reminder deleted on the way in. The meeting stands where it was and so must its
    /// reminder.
    /// </summary>
    [Fact]
    public async Task Reschedule_RefusedAfterTheRelease_KeepsTheReminder()
    {
        using var app = NewApp();
        var day = Day();
        var token = await ManageTokenOfAsync(await CreateAsync(app, day + FirstSlotTime));

        var response = await RescheduleAsync(app, token, day + NeverOfferedTime);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        app.Read(context => context.Reminders.AsNoTracking().Single()).DueAt.ShouldBe(
            ReminderSchedule.DueAt(BookingMover.ParseInstant(day + FirstSlotTime)).ToUnixTimeSeconds());
    }

    /// <summary>
    /// One confirmed meeting on a seeded link, with whatever reminder the writer decided it gets,
    /// returned tracked because the mover mutates it.
    /// </summary>
    private static Booking Book(KairosDbContext context, Instant start)
    {
        Seed(context);

        var created = Writer(context)
            .TryCreate(LinkId, Slug, start, Meeting, Booker, string.Empty, NoMargin);
        created.Success.ShouldBeTrue();

        return context.Bookings.Single(row => row.Id == created.Id);
    }

    /// <summary>
    /// The mover as the composition root builds it. The slot query is real but never reached:
    /// <see cref="BookingMover.MoveToAsync"/> places a booking, and asking whether the new time is
    /// offered is <see cref="BookingMover.ReleaseAndCheckAsync"/>'s job, one call earlier.
    /// </summary>
    private static BookingMover Mover(KairosDbContext context) =>
        new(context, new SlotQuery(context, new AvailabilityQuery(context), new SlotGenerator(), []));

    /// <summary>
    /// The canceller as the composition root builds it, on the same clock as
    /// <see cref="Writer"/> — the cancellation stamp is not what these cases read, but two clocks
    /// in one test are two answers.
    /// </summary>
    private static BookingCanceller Canceller(KairosDbContext context)
    {
        var clock = new FixedClock(Now);
        return new BookingCanceller(
            context,
            new OutboxWriter(context, clock),
            new FollowUpWriter(context, new SecretProtector(TestKey), clock),
            new FollowUpSignal(),
            new ProposalWriter(context, clock),
            clock);
    }

    /// <summary>
    /// The writer as the composition root builds it, on a clock these tests can place relative to
    /// <see cref="ReminderSchedule.DueAt"/> — the real one cannot promise which side of the lead
    /// the seeded meeting falls on.
    /// </summary>
    private static BookingWriter Writer(KairosDbContext context)
    {
        var clock = new FixedClock(Now);
        return new BookingWriter(
            context,
            clock,
            new OutboxWriter(context, clock),
            new FollowUpWriter(context, new SecretProtector(TestKey), clock));
    }

    private static KairosApp NewApp()
    {
        var app = new KairosApp();
        app.Seed(Seed);
        return app;
    }

    private static void Seed(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        context.Patterns.Add(pattern);

        // The rule and the duration are what the endpoint-driven cases need, because they ask the
        // real slot generator what this link offers. The cases that call the mover directly never
        // reach it, and rows they ignore cost them nothing.
        context.PatternRules.Add(new PatternRule
        {
            Id = Guid.CreateVersion7(),
            PatternId = pattern.Id,
            IsoDayOfWeek = (int)IsoDayOfWeek.Monday,
            StartMinute = NineAmMinute,
            EndMinute = FivePmMinute,
        });
        context.Links.Add(new Link
        {
            Id = LinkId,
            Slug = Slug,
            Name = LinkName,
            PatternId = pattern.Id,
            MarginMinutes = NoMargin,
        });
        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = MeetingMinutes, SortOrder = OnlyDurationOrder });
        context.SaveChanges();
    }

    /// <summary>
    /// The Monday the endpoint-driven cases book on. Computed rather than written down: the
    /// application resolves the real clock, so a literal date would turn them into tests that pass
    /// until they silently stop.
    /// </summary>
    private static string Day() => LocalDatePattern.Iso.Format(
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday));

    private static Task<HttpResponseMessage> CreateAsync(KairosApp app, string start) =>
        app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start,
                durationMinutes = MeetingMinutes,
                name = Booker.Name,
                email = Booker.Email,
                timeZone = Booker.TimeZoneId,
            },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> RescheduleAsync(KairosApp app, string? manageToken, string start) =>
        app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{manageToken}/reschedule",
            new { start },
            TestContext.Current.CancellationToken);

    private static async Task<string?> ManageTokenOfAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return created.GetProperty("manageToken").GetString();
    }
}
