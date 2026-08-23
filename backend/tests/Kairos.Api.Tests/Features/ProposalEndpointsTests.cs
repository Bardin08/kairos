using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Availability;
using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using MimeKit;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The proposal's whole life from the outside: offered, held, answered — and every way it can be
/// refused. The hold itself is pinned in <see cref="ProposalHoldTests"/>; what is asserted here is
/// that these routes create, close and honour one.
/// </summary>
public class ProposalEndpointsTests
{
    private const string MovedTo = $"{MeetingsFixture.Day}T14:00:00Z";
    private const string OutsideHours = $"{MeetingsFixture.Day}T03:00:00Z";

    /// <summary>
    /// A second offered hour, and it has to be a different one. The first proposal HOLDS the slot
    /// it named, so proposing that same hour again is refused by the availability re-check and
    /// never reaches the index this test is about.
    /// </summary>
    private const string MovedAgainTo = $"{MeetingsFixture.Day}T15:00:00Z";

    private const string BookedEnd = $"{MeetingsFixture.Day}T11:30:00Z";

    /// <summary>
    /// The slot immediately after the meeting, and therefore inside the margin the meeting pads
    /// its own slot with. Nothing may offer it while that meeting is confirmed — which is the
    /// whole point of it being the one both release paths are asserted on. Spelled as the
    /// booking's own end rather than repeated, so changing the fixture's duration cannot leave
    /// this pointing at an hour that is no longer adjacent to anything.
    /// </summary>
    private const string MovedNextTo = BookedEnd;
    private const string MovedToEnd = $"{MeetingsFixture.Day}T14:30:00Z";

    /// <summary>Lower-cased <see cref="ProposalState.Open"/>, as the invitee's page reads it.</summary>
    private const string OpenState = "open";

    /// <summary>An expiry long past, so the lapse is the row's own state rather than a wait.</summary>
    private const long LapsedAt = 1_000_000_000;

    private const string UnknownToken = "not-a-token";

    private const int SecondsPerMinute = 60;

    private const int NoProposal = 0;
    private const int OneProposal = 1;
    private const int TwoProposals = 2;

    private const string RelayHost = "smtp.example.com";
    private const string RelayFrom = "organizer@example.com";
    private const string RelayBaseUrl = "https://kairos.example.com";

    /// <summary>What a relay answers a message it took. Never read; it only has to exist.</summary>
    private const string RelayAccepted = "2.0.0 Ok: queued";

    private const string RelayRefused = "The relay dropped the connection.";

    private const string BusySourceBroken = "The calendar provider fell over.";

    /// <summary>Lower-cased <see cref="ProposalState.Expired"/>, as the invitee's page reads it.</summary>
    private const string ExpiredState = "expired";

    private const string AcceptAction = "accept";
    private const string DeclineAction = "decline";

    [Fact]
    public async Task Propose_AnOfferedSlot_OpensAProposalAndLeavesTheBookingWhereItIs()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var before = app.Read(context => context.Bookings.Single(row => row.Id == booking).StartUtc);

        var answer = await ProposeAsync(client, booking, MovedTo);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.Bookings.Single(row => row.Id == booking).StartUtc).ShouldBe(before);
        app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking))
            .State.ShouldBe(ProposalState.Open);
    }

    /// <summary>
    /// The gate that keeps the invitee-side ask honestly cut: nothing in this feature writes a
    /// booking the availability rules would not have produced. An hour the generator does not
    /// offer is refused with the same code the public create path answers with.
    /// </summary>
    [Fact]
    public async Task Propose_AnHourTheLinkDoesNotOffer_IsRefused()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, OutsideHours);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(BookingsEndpoints.SlotUnavailableCode);
    }

    /// <summary>
    /// A proposal nobody can be told about is not a degraded proposal, it is a slot held hostage.
    /// </summary>
    /// <remarks>
    /// The token exists only inside the call that mints it — the database keeps its SHA-256 — so a
    /// hold committed without a mail going out is unanswerable for the whole window and nothing
    /// anywhere can recover it. Rule 14 says a provider being down never fails a BOOKING; this is
    /// not one, and the mailer's own tests call an unconfigured relay this deployment's ordinary
    /// state, which is exactly why it has to be refused rather than swallowed.
    /// </remarks>
    [Fact]
    public async Task Propose_NoRelayConfigured_IsRefusedAndOpensNoHold()
    {
        using var app = new KairosApp();

        // Deliberately not OperatorWithRelay: this is a fresh deployment, mail never set up.
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, MovedTo);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(MeetingsAdminEndpoints.MailUnconfiguredCode);
        app.Read(context => context.BookingProposals.Count()).ShouldBe(NoProposal);
    }

    /// <summary>
    /// And the relay that is configured and refuses. The hold is committed by the time anything
    /// is sent, so it is given straight back and the operator told: a Withdrawn proposal and an
    /// error beats a dead hold and a 200.
    /// </summary>
    [Fact]
    public async Task Propose_ARelayThatRefusesTheMessage_GivesTheHeldSlotBack()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app, new RefusingRelay());
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, MovedTo);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(MeetingsAdminEndpoints.ProposalUndeliverableCode);
        app.Read(context => context.BookingProposals.Count(row => row.State == ProposalState.Open))
            .ShouldBe(NoProposal);

        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldContain(MovedTo);
    }

    /// <summary>
    /// Push the meeting back half an hour — the commonest reason to propose anything — and the
    /// case the propose check refused outright until it released the booking first. A confirmed
    /// meeting pads its own slot by the margin, so the hour directly after it is blocked by the
    /// very meeting being moved.
    /// </summary>
    [Fact]
    public async Task Propose_TheSlotDirectlyAfterTheMeeting_IsOfferedBecauseTheMeetingIsReleased()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, MovedNextTo);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        app.Read(context => context.BookingProposals.Single(candidate => candidate.BookingId == booking))
            .State.ShouldBe(ProposalState.Open);
    }

    /// <summary>
    /// The compensation, and the only reason releasing here is safe at all. This propose has
    /// already cancelled the booking to run its re-check; without the restore the operator reads
    /// a refusal and the meeting is off in the database with nobody told and nothing withdrawn.
    /// </summary>
    [Fact]
    public async Task Propose_AnHourTheLinkDoesNotOffer_LeavesTheMeetingConfirmedWhereItWas()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, OutsideHours);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(MeetingsFixture.BookedInstant);
        app.Read(context => context.BookingProposals.Count(candidate => candidate.BookingId == booking))
            .ShouldBe(NoProposal);
    }

    [Fact]
    public async Task Propose_ASecondProposalWhileOneIsOpen_IsRefused()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);

        await ProposeAsync(client, booking, MovedTo);
        var again = await ProposeAsync(client, booking, MovedAgainTo);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(MeetingsAdminEndpoints.ProposalOpenCode);

        // And the refusal costs the meeting nothing. This route releases the booking to run its
        // check, so every way out of it has to put the booking back exactly as it was — the second
        // proposal is not written, and neither is a second hold.
        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(MeetingsFixture.BookedInstant);
        app.Read(context => context.BookingProposals.Count(candidate => candidate.BookingId == booking))
            .ShouldBe(OneProposal);
    }

    /// <summary>
    /// The same refusal, one hold later, and it must NOT happen.
    /// </summary>
    /// <remarks>
    /// The design's central claim is that nothing waits on <c>ProposalSweeper</c>: every reader of
    /// a hold compares <see cref="BookingProposal.ExpiresAt"/> against the clock, so a lapsed hold
    /// stops holding at the instant it lapses whether or not anything has been round to mark it.
    /// This route was the one place that read <see cref="ProposalState.Open"/> without the clock,
    /// which left the operator unable to suggest a new time until a tick fired or they withdrew a
    /// dead suggestion by hand — availability right throughout, and re-proposing wrong.
    ///
    /// Nothing sweeps in this test, and that is the assertion. The lapse is written straight onto
    /// the row and no sweeper runs against it.
    /// </remarks>
    [Fact]
    public async Task Propose_TheOpenHoldHasLapsed_IsAllowedWithoutWaitingForASweep()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);

        await ProposeAsync(client, booking, MovedTo);
        Lapse(app, booking);

        var again = await ProposeAsync(client, booking, MovedAgainTo);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);

        var proposals = app.Read(context => context.BookingProposals.AsNoTracking()
            .Where(row => row.BookingId == booking)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToList());
        proposals.Count.ShouldBe(TwoProposals);

        // The dead one is CLOSED rather than ignored, and the difference is the filtered unique
        // index over BookingId WHERE State = Open: that is a row and not a read, so a lapsed one
        // left Open would still occupy the slot the new hold needs.
        proposals[0].State.ShouldBe(ProposalState.Expired);
        proposals[0].DecidedAt.ShouldNotBeNull();

        // And left unannounced on purpose. The sweeper picks up Expired rows with no NotifiedAt
        // precisely so the operator still hears that their earlier suggestion lapsed — closing it
        // here must hand that notice on rather than swallow it.
        proposals[0].NotifiedAt.ShouldBeNull();

        proposals[1].State.ShouldBe(ProposalState.Open);
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposals[1].StartUtc))
            .ShouldBe(MovedAgainTo);

        // The meeting itself never moved. Proposing is asking.
        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(row.StartUtc))
            .ShouldBe(MeetingsFixture.BookedInstant);
    }

    /// <summary>
    /// The invitee moves the meeting themselves while a suggestion is open.
    /// </summary>
    /// <remarks>
    /// The suggestion is about a meeting that is no longer where it was, and leaving it Open costs
    /// twice: the slot it names is blocked for the rest of the hold window, and its token still
    /// resolves — so accepting it afterwards moves the meeting a second time, away from a current
    /// time the invitee never agreed to and never saw. The cancel path has closed holds since it
    /// was written; this path had not, because the two are separate copies of the same release.
    /// </remarks>
    [Fact]
    public async Task RescheduleBooking_WhileAProposalIsOpen_ClosesTheHoldAndFreesItsSlot()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        await ProposeAsync(client, booking, MovedTo);
        var manage = KnownManageToken(app, booking);

        var answer = await app.Client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings/{manage}/reschedule",
            new RescheduleBookingRequest { Start = MovedAgainTo },
            TestContext.Current.CancellationToken);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposal = app.Read(context =>
            context.BookingProposals.Single(row => row.BookingId == booking));
        proposal.State.ShouldBe(ProposalState.Withdrawn);
        proposal.DecidedAt.ShouldNotBeNull();
        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldContain(MovedTo);
    }

    [Fact]
    public async Task Withdraw_AnOpenProposal_ClosesItAndReleasesTheHold()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        await ProposeAsync(client, booking, MovedTo);
        var proposal = app.Read(context =>
            context.BookingProposals.Single(row => row.BookingId == booking).Id);

        var answer = await client.DeleteAsync(
            $"{ApiVersions.V1}/admin/meetings/{booking}/proposals/{proposal}",
            TestContext.Current.CancellationToken);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Read(context => context.BookingProposals.Single(row => row.Id == proposal))
            .State.ShouldBe(ProposalState.Withdrawn);
        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldContain(MovedTo);
    }

    [Fact]
    public async Task Accept_AnOpenProposal_MovesTheBookingAndAdvancesTheSequence()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);
        var before = app.Read(context => context.Bookings.Single(row => row.Id == booking).Sequence);

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var moved = app.Read(context => context.Bookings.Single(row => row.Id == booking));
        moved.Status.ShouldBe(BookingStatus.Confirmed);
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(moved.StartUtc)).ShouldBe(MovedTo);
        moved.Sequence.ShouldBe(before + 1);

        // And the row records the answer. Left Open it would hold the slot the meeting now sits
        // in, refuse every later proposal through the filtered unique index, and eventually reach
        // the sweeper as a suggestion nobody answered.
        app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking))
            .State.ShouldBe(ProposalState.Accepted);
    }

    /// <summary>
    /// The accept's own release, and the only case that pins it. The meeting pads its own slot by
    /// the margin, so the hour directly after it is covered by the meeting being moved: released,
    /// the re-check sees a free hour; left confirmed, the acceptance is refused by its own subject.
    /// </summary>
    [Fact]
    public async Task Accept_AProposalOnTheSlotDirectlyAfterTheMeeting_StillMovesIt()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking, MovedNextTo);

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var moved = app.Read(context => context.Bookings.Single(row => row.Id == booking));
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(moved.StartUtc))
            .ShouldBe(MovedNextTo);
    }

    /// <summary>The old slot is free the moment the move is real, and on every link.</summary>
    [Fact]
    public async Task Accept_AnOpenProposal_ReleasesTheOriginalSlot()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);

        await AnswerAsync(app, token, AcceptAction);

        (await MeetingsFixture.SlotsOnOtherLinkAsync(app)).ShouldContain(MeetingsFixture.BookedInstant);
    }

    /// <summary>A declined proposal leaves the meeting exactly as it was, asserted field by field.</summary>
    [Fact]
    public async Task Decline_AnOpenProposal_LeavesTheBookingExactlyAsItWas()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);
        var before = app.Read(context => context.Bookings.AsNoTracking().Single(row => row.Id == booking));

        var answer = await AnswerAsync(app, token, DeclineAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = app.Read(context => context.Bookings.AsNoTracking().Single(row => row.Id == booking));
        after.StartUtc.ShouldBe(before.StartUtc);
        after.EndUtc.ShouldBe(before.EndUtc);
        after.Sequence.ShouldBe(before.Sequence);
        after.Status.ShouldBe(before.Status);
        app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking))
            .State.ShouldBe(ProposalState.Declined);
    }

    [Fact]
    public async Task Accept_AProposalAlreadyDeclined_IsRefused()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);
        await AnswerAsync(app, token, DeclineAction);

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(BookingsEndpoints.ProposalClosedCode);
    }

    [Fact]
    public async Task Accept_AProposalWhoseHoldHasLapsed_IsRefused()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);

        Lapse(app, booking);

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(BookingsEndpoints.ProposalExpiredCode);
    }

    /// <summary>
    /// A failure that is not a refusal must not answer for the invitee.
    /// </summary>
    /// <remarks>
    /// The compensating close writes a decision onto the proposal, and only a refusal is a
    /// decision. A provider falling over — or, the case that prompted this, an invitee closing the
    /// tab and cancelling the request — is not the invitee having lost their slot, and burning the
    /// row over it costs them a suggestion they never saw. The restore still runs, because that
    /// half compensates a write this request already made and has to happen whatever went wrong.
    /// </remarks>
    [Fact]
    public async Task Accept_TheReCheckFailingForAReasonThatIsNotARefusal_LeavesTheProposalOpen()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);

        using var broken = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddScoped<IBusySource, ThrowingBusySource>())).CreateClient();

        var answer = await broken.PostAsync(
            $"{ApiVersions.V1}/bookings/proposals/{token}/{AcceptAction}",
            content: null,
            TestContext.Current.CancellationToken);

        answer.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking))
            .State.ShouldBe(ProposalState.Open);

        var row = app.Read(context => context.Bookings.Single(candidate => candidate.Id == booking));
        row.Status.ShouldBe(BookingStatus.Confirmed);
        row.CancelledAt.ShouldBeNull();
    }

    /// <summary>
    /// The hardest path out, and the one nothing else reaches: the slot went away while the
    /// invitee was reading. Everything goes back — the meeting to the hour it was already at, and
    /// the proposal to Withdrawn rather than to Open, because the slot it was holding is gone and
    /// re-offering it would be offering something that is not there.
    /// </summary>
    [Fact]
    public async Task Accept_AProposalWhoseSlotIsNoLongerOffered_PutsEverythingBack()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);
        TakeTheProposedSlot(app);

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var after = app.Read(context => context.Bookings.Single(row => row.Id == booking));
        after.Status.ShouldBe(BookingStatus.Confirmed);
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(after.StartUtc))
            .ShouldBe(MeetingsFixture.BookedInstant);
        app.Read(context => context.BookingProposals.Single(row => row.BookingId == booking))
            .State.ShouldBe(ProposalState.Withdrawn);
    }

    /// <summary>
    /// Both times, in the order the page draws them. A run of same-typed ISO strings in one
    /// positional record is exactly the shape where the current pair and the proposed pair swap
    /// without a compiler anywhere, and the invitee is asked to agree to whichever one is printed
    /// second.
    /// </summary>
    [Fact]
    public async Task GetProposal_AnOpenProposal_TellsTheInviteeBothTimesAndThatNothingIsDecided()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);

        var answer = await app.Client.GetFromJsonAsync<ProposalResponse>(
            $"{ApiVersions.V1}/bookings/proposals/{token}", TestContext.Current.CancellationToken);

        answer!.BookingId.ShouldBe(booking);
        answer.CurrentStart.ShouldBe(MeetingsFixture.BookedInstant);
        answer.CurrentEnd.ShouldBe(BookedEnd);
        answer.ProposedStart.ShouldBe(MovedTo);
        answer.ProposedEnd.ShouldBe(MovedToEnd);
        answer.State.ShouldBe(OpenState);
    }

    /// <summary>
    /// A hold whose expiry has passed has stopped holding, whatever its row still says, and the
    /// page has to be told that rather than shown Accept and Decline over a suggestion that will
    /// answer 409 to either. Every other reader applies the expiry against the clock; this one
    /// used to be the exception.
    /// </summary>
    [Fact]
    public async Task GetProposal_AProposalWhoseHoldHasLapsed_ReadsAsExpired()
    {
        using var app = new KairosApp();
        using var client = OperatorWithRelay(app);
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);
        Lapse(app, booking);

        var answer = await app.Client.GetFromJsonAsync<ProposalResponse>(
            $"{ApiVersions.V1}/bookings/proposals/{token}", TestContext.Current.CancellationToken);

        answer!.State.ShouldBe(ExpiredState);
    }

    [Fact]
    public async Task GetProposal_AnUnknownToken_Answers404()
    {
        using var app = new KairosApp();

        var answer = await app.Client.GetAsync(
            $"{ApiVersions.V1}/bookings/proposals/{UnknownToken}", TestContext.Current.CancellationToken);

        answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient client, Guid booking, string start) =>
        client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/meetings/{booking}/proposals",
            new ProposeMeetingRequest { Start = start },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> AnswerAsync(KairosApp app, string token, string action) =>
        app.Client.PostAsync(
            $"{ApiVersions.V1}/bookings/proposals/{token}/{action}",
            content: null,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Proposes <paramref name="start"/> and hands back a token that opens it.
    /// </summary>
    /// <remarks>
    /// The plaintext never leaves the mailer, so the row is re-minted with a token this test
    /// knows: the database holds only a SHA-256, and re-hashing a known string is the only way
    /// back to something the invitee's route will resolve.
    /// </remarks>
    private static async Task<string> ProposeWithKnownTokenAsync(
        KairosApp app,
        HttpClient client,
        Guid booking,
        string start = MovedTo)
    {
        await ProposeAsync(client, booking, start);

        var known = Guid.CreateVersion7().ToString("N");
        app.Seed(context =>
        {
            var proposal = context.BookingProposals.Single(row => row.BookingId == booking);
            proposal.TokenHash = BookingWriter.Hash(known);
        });

        return known;
    }

    /// <summary>
    /// A confirmed meeting on the proposed hour, written straight in. It could not have been
    /// BOOKED there — the hold is doing its job — so this is the state that arrives from outside:
    /// the organizer's own calendar fills the slot the invitee was offered before they answer.
    /// </summary>
    private static void TakeTheProposedSlot(KairosApp app) => app.Seed(context =>
    {
        var link = context.Links.Single(row => row.Slug == MeetingsFixture.OtherSlug);
        var start = InstantPattern.ExtendedIso.Parse(MovedTo).Value.ToUnixTimeSeconds();

        context.Bookings.Add(new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = start,
            EndUtc = start + (MeetingsFixture.DurationMinutes * SecondsPerMinute),
            Status = BookingStatus.Confirmed,
            ManageTokenHash = Guid.CreateVersion7().ToString("N"),
        });
    });

    /// <summary>
    /// An operator's client on a deployment whose relay works.
    /// </summary>
    /// <remarks>
    /// Every test here proposes, and a propose is refused outright where no relay is configured —
    /// so a working one is this suite's world rather than a detail of any one case. Two things are
    /// replaced and nothing else: the <c>Email</c> section, exactly as an operator sets it, and the
    /// socket at the very bottom.
    /// </remarks>
    private static HttpClient OperatorWithRelay(KairosApp app, EmailSender? relay = null) =>
        WithRelayConfigured(app).CreateOperatorClient(services =>
        {
            services.RemoveAll<EmailSender>();
            services.AddSingleton(relay ?? new AcceptingRelay());
        });

    private static WebApplicationFactory<KairosDbContext> WithRelayConfigured(KairosApp app) =>
        app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{EmailOptions.SectionName}:{nameof(EmailOptions.Host)}", RelayHost);
            builder.UseSetting($"{EmailOptions.SectionName}:{nameof(EmailOptions.From)}", RelayFrom);
            builder.UseSetting(
                $"{EmailOptions.SectionName}:{nameof(EmailOptions.PublicBaseUrl)}", RelayBaseUrl);
        });

    /// <summary>
    /// A relay that takes everything. Substituted at the socket rather than mocked at the protocol:
    /// what is under test is what the route does with the answer, and <see cref="EmailSender"/>
    /// itself is verified by mail arriving in an inbox.
    /// </summary>
    private sealed class AcceptingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(RelayAccepted);
    }

    /// <summary>A relay that is configured and will not take it — the failure rule 14 swallows.</summary>
    private sealed class RefusingRelay() : EmailSender(Options.Create(new EmailOptions()))
    {
        public override Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
            throw new IOException(RelayRefused);
    }

    /// <summary>
    /// Re-mints the booking's manage token to one this test knows, the same way
    /// <see cref="ProposeWithKnownTokenAsync"/> does and for the same reason: only the SHA-256 is
    /// stored, and the plaintext left the process in the 201 that created it.
    /// </summary>
    private static string KnownManageToken(KairosApp app, Guid booking)
    {
        var known = Guid.CreateVersion7().ToString("N");
        app.Seed(context =>
            context.Bookings.Single(row => row.Id == booking).ManageTokenHash = BookingWriter.Hash(known));

        return known;
    }

    /// <summary>
    /// Pushes the hold's expiry into the past. Reached through the database rather than through a
    /// clock substitution: the expiry is a stored instant, and moving it back is the same state a
    /// real lapse produces.
    /// </summary>
    private static void Lapse(KairosApp app, Guid booking) => app.Seed(context =>
        context.BookingProposals.Single(row => row.BookingId == booking).ExpiresAt = LapsedAt);

    /// <summary>
    /// A calendar source that is having a bad day. Not a refusal, and not a decision.
    /// </summary>
    /// <remarks>Qualified: NodaTime has an Interval too, and both namespaces are in scope here.</remarks>
    private sealed class ThrowingBusySource : IBusySource
    {
        public Task<IReadOnlyList<BusyBlock>> GetBusyAsync(
            Kairos.Api.Scheduling.Interval window,
            CancellationToken cancellationToken) =>
            throw new IOException(BusySourceBroken);
    }
}
