using System.Net;
using System.Net.Http.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

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

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient client, Guid booking, string start) =>
        client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/meetings/{booking}/proposals",
            new ProposeMeetingRequest { Start = start },
            TestContext.Current.CancellationToken);
}
