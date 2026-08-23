using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

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
}
