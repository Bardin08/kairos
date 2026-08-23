using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The operator's cancel, which until now did not exist: the only way out of a meeting was the
/// invitee's own manage link, or editing the calendar by hand and leaving it disagreeing with
/// Kairos.
/// </summary>
public class OperatorCancelTests
{
    private const string Reason = "I have to be somewhere else.";

    private const string ReasonCharacter = "x";

    private const int OverLongReasonLength = CancelMeetingRequest.MaxReasonLength + 1;

    private const string ExternalEventId = "google-event-1";
    private const int OneWithdrawal = 1;

    private const string ProposedInstant = $"{MeetingsFixture.Day}T14:00:00Z";
    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    private const int SeededSequence = 3;
    private const int RacingCallers = 2;
    private const int SequenceAdvancedOnce = 1;
    private const int OneAnnouncement = 1;
    private const int NoAnnouncement = 0;

    private const string SeededBookerName = "Grace Hopper";
    private const string SeededBookerEmail = "grace@example.com";
    private const string SeededBookerZone = "Europe/Warsaw";

    /// <summary>
    /// Generous, and it only ever bounds a failure: two requests that meet do so in microseconds,
    /// and one that never arrives would otherwise hang the run instead of failing it.
    /// </summary>
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Cancel_AConfirmedMeeting_CancelsItAndKeepsTheReason()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await CancelAsync(client, booking, Reason);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Bookings.Single(row => row.Id == booking))
            .ShouldSatisfyAllConditions(
                row => row.Status.ShouldBe(BookingStatus.Cancelled),
                row => row.CancellationReason.ShouldBe(Reason),
                row => row.CancelledAt.ShouldNotBeNull());
    }

    /// <summary>
    /// RFC 5546: a CANCEL has to outrank the REQUEST it withdraws, or the client discards it in
    /// silence and the meeting stays in the invitee's calendar for a booking that is off.
    /// </summary>
    [Fact]
    public async Task Cancel_AConfirmedMeeting_AdvancesTheSequence()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);
        var before = app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence);

        await CancelAsync(client, booking, Reason);

        app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence)
            .ShouldBe(before + 1);
    }

    /// <summary>
    /// The same idempotence the invitee's cancel has: an operator who double-clicks gets the
    /// answer they can already see, not a 500 from the outbox's unique index or a sequence
    /// advanced twice.
    /// </summary>
    /// <remarks>
    /// The sequence alone is weakly held — the unique index would keep it still. The second press
    /// must also not run the tail: withdraw an event a calendar already released, mail a second
    /// CANCEL. The withdrawal is counted and the mail is not, because
    /// <see cref="Kairos.Api.Integrations.Email.EmailSender"/> has no seam to mock; it is pinned
    /// by proxy, the withdrawal and the send being adjacent statements with no branch between.
    /// </remarks>
    [Fact]
    public async Task Cancel_AMeetingAlreadyCancelled_AnswersTheSameAndRunsTheTailOnlyOnce()
    {
        var calendar = new CountingCalendar();
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient(services =>
        {
            services.RemoveAll<ICalendarWriter>();
            services.AddScoped<ICalendarWriter>(_ => calendar);
        });

        var booking = await MeetingsFixture.BookAsync(app, client);

        await CancelAsync(client, booking, Reason);
        var sequence = app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence);

        var again = await CancelAsync(client, booking, Reason);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence).ShouldBe(sequence);
        calendar.Withdrawals.ShouldBe(OneWithdrawal);
    }

    /// <summary>The gate the roadmap names: the slot comes back on every link, margin included.</summary>
    [Fact]
    public async Task Cancel_AConfirmedMeeting_PutsTheSlotBackOnEveryLink()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldNotContain(MeetingsFixture.BookedInstant);

        await CancelAsync(client, booking, Reason);

        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldContain(MeetingsFixture.BookedInstant);
    }

    /// <summary>
    /// A hold on a meeting that is off is a slot nobody will ever take. It is closed in the same
    /// save as the cancellation, so there is no window in which the booking is off and its hold is
    /// still holding.
    /// </summary>
    [Fact]
    public async Task Cancel_AMeetingHoldingAnOpenProposal_ClosesTheHoldInTheSameSave()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);
        Hold(app, booking);

        await CancelAsync(client, booking, Reason);

        var proposal = app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking));
        proposal.State.ShouldBe(ProposalState.Withdrawn);
        proposal.DecidedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// A meeting that reached Cancelled without this endpoint announcing it — the case the early
    /// return covers and the unique index cannot, there being no outbox row to collide with.
    /// Nothing may happen: no second sequence, no reason written over somebody else's, no CANCEL.
    /// </summary>
    [Fact]
    public async Task Cancel_AMeetingCancelledWithNoAnnouncementBehindIt_ChangesNothing()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = Guid.CreateVersion7();
        Seeded(app, booking);

        var answer = await CancelAsync(client, booking, Reason);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Bookings.Single(row => row.Id == booking))
            .ShouldSatisfyAllConditions(
                row => row.Sequence.ShouldBe(SeededSequence),
                row => row.CancellationReason.ShouldBeNull());

        app.Read(context => context.OutboxMessages.Count(message => message.BookingId == booking))
            .ShouldBe(NoAnnouncement);
    }

    /// <summary>
    /// Two cancels genuinely in flight at once — the path the early return above cannot reach,
    /// because both callers read the booking as Confirmed before either writes.
    /// </summary>
    /// <remarks>
    /// <see cref="BookingConcurrencyTests"/> barriers two threads on one database; this needs two
    /// whole requests, so the barrier goes into an EF interceptor holding each caller inside
    /// <c>SaveChangesAsync</c>, after changes are tracked and before any SQL — both mid-save
    /// together, the state the outbox's unique index on (BookingId, Type) decides. Waiting on the
    /// save rather than a clock read is what stops this going vacuous: the interceptor takes
    /// nothing but a save carrying this booking's transition to Cancelled, asserted through
    /// <see cref="SaveRendezvous.BothArrived"/>.
    /// </remarks>
    [Fact]
    public async Task Cancel_TwoCancelsInFlightAtOnce_CancelsOnceAndAnnouncesOnce()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        using var rendezvous = new SaveRendezvous(RacingCallers, RendezvousTimeout);
        using var racers = app.CreateOperatorClient(services =>
        {
            // The options are rebuilt exactly as KairosApp builds them, plus the interceptor:
            // same context, same provider, same connection string, so the process still has the
            // one model AvailabilityQuery's compiled queries are bound to.
            services.RemoveAll<DbContextOptions<KairosDbContext>>();
            services.AddSingleton(new DbContextOptionsBuilder<KairosDbContext>()
                .UseSqlite(app.ConnectionString)
                .AddInterceptors(rendezvous)
                .Options);
        });

        rendezvous.Arm(booking);

        var answers = await Task.WhenAll(
            CancelAsync(racers, booking, Reason),
            CancelAsync(racers, booking, Reason));

        rendezvous.BothArrived.ShouldBeTrue();
        answers.ShouldAllBe(answer => answer.StatusCode == HttpStatusCode.OK);
        answers.ShouldAllBe(answer => answer.StatusCode == HttpStatusCode.OK);

        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Cancelled);
        row.Sequence.ShouldBe(SequenceAdvancedOnce);

        // The half the unique index is actually protecting. A second row here is a second
        // booking.cancelled on the wire for one cancellation; a 500 instead of this row is the
        // operator being told their cancel failed on a meeting that is off.
        app.Read(context => context.OutboxMessages.Count(message =>
                (message.BookingId == booking) && (message.Type == OutboxMessageType.BookingCancelled)))
            .ShouldBe(OneAnnouncement);
    }

    [Fact]
    public async Task Cancel_AnUnknownMeeting_Answers404()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();

        var answer = await CancelAsync(client, Guid.CreateVersion7(), Reason);

        answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Rule 20's shape at the body: an operator field is still a bounded field.</summary>
    [Fact]
    public async Task Cancel_AReasonLongerThanTheBound_Answers400()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await CancelAsync(
            client, booking, string.Concat(Enumerable.Repeat(ReasonCharacter, OverLongReasonLength)));

        answer.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid id, string reason) =>
        client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/meetings/{id}/cancel",
            new CancelMeetingRequest { Reason = reason },
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A live hold on a slot away from the booking's own, written straight in: nothing in this
    /// pull request mints a proposal, and what is being asserted is that a cancellation closes one
    /// it finds rather than that anything creates it.
    /// </summary>
    private static void Hold(KairosApp app, Guid bookingId) => app.Seed(context =>
    {
        var proposed = InstantPattern.ExtendedIso.Parse(ProposedInstant).Value.ToUnixTimeSeconds();
        var now = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds();

        context.BookingProposals.Add(new BookingProposal
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            StartUtc = proposed,
            EndUtc = proposed + (MeetingsFixture.DurationMinutes * SecondsPerMinute),
            ExpiresAt = now + (SchedulingDefaults.ProposalHoldHours * SecondsPerHour),
            CreatedAt = now,
            State = ProposalState.Open,
            TokenHash = Guid.CreateVersion7().ToString("N"),
        });
    });

    /// <summary>
    /// A calendar that is connected, accepts the booking's event and counts what is withdrawn from
    /// it. Shared across every request in a test on purpose — the question is how many times the
    /// rule-14 tail ran in total, not what any one request did.
    /// </summary>
    private sealed class CountingCalendar : ICalendarWriter
    {
        private readonly Lock _gate = new();
        private int _withdrawals;

        public int Withdrawals
        {
            get
            {
                lock (_gate)
                {
                    return _withdrawals;
                }
            }
        }

        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(ExternalEventId);

        public Task UpdateEventAsync(
            string externalEventId,
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _withdrawals++;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A meeting already off, written straight in so that nothing about it has passed through the
    /// cancel endpoint: no outbox row, and a sequence some earlier message was composed at.
    /// </summary>
    private static void Seeded(KairosApp app, Guid bookingId)
    {
        MeetingsFixture.Seed(app);
        app.Seed(context =>
        {
            var link = context.Links.Single(candidate => candidate.Slug == MeetingsFixture.Slug);
            var start = InstantPattern.ExtendedIso.Parse(MeetingsFixture.BookedInstant).Value
                .ToUnixTimeSeconds();

            context.Bookings.Add(new Booking
            {
                Id = bookingId,
                LinkId = link.Id,
                StartUtc = start,
                EndUtc = start + (MeetingsFixture.DurationMinutes * SecondsPerMinute),
                Status = BookingStatus.Cancelled,
                Sequence = SeededSequence,
                ManageTokenHash = Guid.CreateVersion7().ToString("N"),
                Participants =
                [
                    new BookingParticipant
                    {
                        Id = Guid.CreateVersion7(),
                        Role = ParticipantRole.Booker,
                        Name = SeededBookerName,
                        Email = SeededBookerEmail,
                        TimeZoneId = SeededBookerZone,
                    },
                ],
            });
        });
    }

    /// <summary>
    /// Holds every caller saving <see cref="Arm"/>'s <see cref="Booking"/> into
    /// <see cref="BookingStatus.Cancelled"/> until all have arrived; invisible to every other
    /// save in the application.
    /// </summary>
    /// <remarks>
    /// The seam is the save rather than the clock, a mocked context or a held write lock: a held
    /// lock surfaces as SQLITE_BUSY and a 503, a different path, and a clock read is one anything
    /// on the route could make. The predicate is deliberately narrow — this booking, modified,
    /// now cancelled — so the seeding, the create and any background save pass straight through.
    /// </remarks>
    private sealed class SaveRendezvous(int participants, TimeSpan timeout) : SaveChangesInterceptor, IDisposable
    {
        private readonly Barrier _barrier = new(participants);
        private readonly Lock _gate = new();
        private int _arrived;
        private Guid _subject;

        /// <summary>True once every participant met the others rather than timing out.</summary>
        public bool BothArrived
        {
            get
            {
                lock (_gate)
                {
                    return _arrived == participants;
                }
            }
        }

        public void Arm(Guid subject)
        {
            lock (_gate)
            {
                _subject = subject;
            }
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (IsTheCancellation(eventData.Context) && _barrier.SignalAndWait(timeout))
            {
                lock (_gate)
                {
                    _arrived++;
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public void Dispose() => _barrier.Dispose();

        private bool IsTheCancellation(DbContext? context)
        {
            Guid subject;
            lock (_gate)
            {
                subject = _subject;
            }

            return (context is not null)
                && (subject != Guid.Empty)
                && context.ChangeTracker.Entries<Booking>().Any(entry =>
                    (entry.State == EntityState.Modified)
                    && (entry.Entity.Id == subject)
                    && (entry.Entity.Status == BookingStatus.Cancelled));
        }
    }
}
