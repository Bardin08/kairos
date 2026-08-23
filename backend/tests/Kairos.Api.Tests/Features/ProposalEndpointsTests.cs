using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.EntityFrameworkCore;

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
    private const string MovedToEnd = $"{MeetingsFixture.Day}T14:30:00Z";

    /// <summary>Lower-cased <see cref="ProposalState.Open"/>, as the invitee's page reads it.</summary>
    private const string OpenState = "open";

    /// <summary>An expiry long past, so the lapse is the row's own state rather than a wait.</summary>
    private const long LapsedAt = 1_000_000_000;

    private const string UnknownToken = "not-a-token";

    private const int SecondsPerMinute = 60;

    private const string AcceptAction = "accept";
    private const string DeclineAction = "decline";

    [Fact]
    public async Task Propose_AnOfferedSlot_OpensAProposalAndLeavesTheBookingWhereItIs()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        var answer = await ProposeAsync(client, booking, OutsideHours);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(BookingsEndpoints.SlotUnavailableCode);
    }

    [Fact]
    public async Task Propose_ASecondProposalWhileOneIsOpen_IsRefused()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);

        await ProposeAsync(client, booking, MovedTo);
        var again = await ProposeAsync(client, booking, MovedAgainTo);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(MeetingsAdminEndpoints.ProposalOpenCode);
    }

    [Fact]
    public async Task Withdraw_AnOpenProposal_ClosesItAndReleasesTheHold()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
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

    /// <summary>The old slot is free the moment the move is real, and on every link.</summary>
    [Fact]
    public async Task Accept_AnOpenProposal_ReleasesTheOriginalSlot()
    {
        using var app = new KairosApp();
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
        var booking = await MeetingsFixture.BookAsync(app, client);
        var token = await ProposeWithKnownTokenAsync(app, client, booking);

        // Reached through the database rather than through a clock substitution: the expiry is a
        // stored instant, and pushing it into the past is the same state a real lapse produces.
        app.Seed(context =>
        {
            var proposal = context.BookingProposals.Single(row => row.BookingId == booking);
            proposal.ExpiresAt = LapsedAt;
        });

        var answer = await AnswerAsync(app, token, AcceptAction);

        answer.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain(BookingsEndpoints.ProposalExpiredCode);
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
        using var client = app.CreateOperatorClient();
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
        using var client = app.CreateOperatorClient();
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
    /// Proposes <see cref="MovedTo"/> and hands back a token that opens it.
    /// </summary>
    /// <remarks>
    /// The plaintext never leaves the mailer, so the row is re-minted with a token this test
    /// knows: the database holds only a SHA-256, and re-hashing a known string is the only way
    /// back to something the invitee's route will resolve.
    /// </remarks>
    private static async Task<string> ProposeWithKnownTokenAsync(
        KairosApp app,
        HttpClient client,
        Guid booking)
    {
        await ProposeAsync(client, booking, MovedTo);

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
}
