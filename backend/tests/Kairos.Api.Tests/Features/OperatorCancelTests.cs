using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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

    /// <summary>The character an over-long reason is built out of, repeated.</summary>
    private const string ReasonCharacter = "x";

    private const int OverLongReasonLength = 2001;

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
    /// The same idempotence the invitee's cancel has, for the same reason: an operator who
    /// double-clicks gets the answer they can already see rather than a 500 from the outbox's
    /// unique index or a sequence advanced twice.
    /// </summary>
    [Fact]
    public async Task Cancel_AMeetingAlreadyCancelled_AnswersTheSameAndAdvancesNothing()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        await CancelAsync(client, booking, Reason);
        var sequence = app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence);

        var again = await CancelAsync(client, booking, Reason);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence).ShouldBe(sequence);
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
    /// A meeting that reached Cancelled without this endpoint having announced it — the case the
    /// early return covers and the unique index cannot, because there is no outbox row to collide
    /// with. Nothing may happen to it: not a second sequence, not a reason written over a
    /// cancellation somebody else made, and not a CANCEL mailed to an invitee who was told months
    /// ago.
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
    /// because both callers read the booking as Confirmed before either has written anything.
    /// </summary>
    /// <remarks>
    /// The overlap is forced rather than hoped for. Both requests are made to rendezvous inside
    /// the handler, at the clock read that sits between loading the booking and saving it, so the
    /// two <c>SaveChanges</c> calls are in flight together and the outbox's unique index on
    /// (BookingId, Type) is what decides. <see cref="BookingConcurrencyTests"/> does the same
    /// thing with a barrier across two threads against one file-backed database; this needs the
    /// whole request, so the barrier goes in the one collaborator the handler reads before it
    /// writes. Without the rendezvous this test would pass on a sequential pair and prove nothing,
    /// which is why <see cref="RendezvousClock.BothArrived"/> is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public async Task Cancel_TwoCancelsInFlightAtOnce_CancelsOnceAndAnnouncesOnce()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        var clock = new RendezvousClock(RacingCallers, RendezvousTimeout);
        using var racers = app.CreateOperatorClient(services =>
        {
            // The pollers read the clock on their own schedule, and a tick landing between the
            // arming and the two requests would take one of the barrier's places. Removed rather
            // than raced with; nothing here is about them.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });

        // The host builds lazily on its first request, and everything it does on the way up reads
        // the clock freely. Armed only once that is behind us.
        (await racers.GetAsync($"{ApiVersions.V1}/admin/meetings", TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        clock.Arm();

        var answers = await Task.WhenAll(
            CancelAsync(racers, booking, Reason),
            CancelAsync(racers, booking, Reason));

        clock.BothArrived.ShouldBeTrue();
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
    /// A clock that holds the first callers after it is armed until all of them have arrived, and
    /// then gets out of the way.
    /// </summary>
    /// <remarks>
    /// The clock rather than a mocked context or a held write lock, because it is the one
    /// collaborator the cancel handler reads after it has loaded the booking and before it saves.
    /// A held lock would produce SQLITE_BUSY and a 503, which is a different path with a different
    /// handler. Only the armed number of calls wait: the handler reads the clock again inside
    /// <see cref="OutboxWriter"/>, and a barrier that caught that one too would deadlock a request
    /// against itself.
    /// </remarks>
    private sealed class RendezvousClock(int participants, TimeSpan timeout) : IClock
    {
        private readonly Barrier _barrier = new(participants);
        private int _waiting;
        private int _arrived;

        /// <summary>True once every participant met the others rather than timing out.</summary>
        public bool BothArrived => Volatile.Read(ref _arrived) == participants;

        public void Arm() => Volatile.Write(ref _waiting, participants);

        public Instant GetCurrentInstant()
        {
            if ((Interlocked.Decrement(ref _waiting) >= 0) && _barrier.SignalAndWait(timeout))
            {
                Interlocked.Increment(ref _arrived);
            }

            return SystemClock.Instance.GetCurrentInstant();
        }
    }

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
}
