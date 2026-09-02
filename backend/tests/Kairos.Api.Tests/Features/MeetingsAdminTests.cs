using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The operator's read of every meeting, and the two facts on it that exist nowhere else: which
/// link a booking came through, and whether it ever reached a calendar.
/// </summary>
/// <remarks>
/// The sync assertions go through the real create path rather than seeded
/// <see cref="CalendarEntry"/> rows, because the claim is about the whole chain — a provider
/// refuses, <see cref="CalendarSyncService"/> records it, the booking survives, this endpoint
/// reports it. A seeded row would pin the last link and pass with the first three broken.
/// </remarks>
public class MeetingsAdminTests
{
    private const string Slug = "consulting";
    private const string OtherSlug = "mentorship";
    private const string LinkName = "Consulting call";
    private const string OtherLinkName = "Mentorship";

    private const int DurationMinutes = 25;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const string FirstSlotTime = "T09:00:00Z";

    /// <summary>Days ahead of the seeding, so real-clock minimum notice never decides a test.</summary>
    private const int WellAhead = 30;

    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";

    /// <summary>Deliberately not the organizer's, which is UTC. Half the point of the column.</summary>
    private const string BookerZone = "Asia/Tokyo";

    private const string Refusal = "Google said no.";
    private const string Revocation = "The grant was withdrawn.";

    /// <summary>
    /// Seeded meetings need instants and nothing else — they never pass through availability — so
    /// they sit on three fixed consecutive days and the range tests can assert on both edges.
    /// </summary>
    private const string FirstDay = "2030-09-09";
    private const string SecondDay = "2030-09-10";
    private const string ThirdDay = "2030-09-11";
    private const string SeededTime = "T11:00:00Z";

    /// <summary>Where a seeded hold suggests moving to, on the same day the meeting sits on.</summary>
    private const string ProposedTime = "T14:00:00Z";

    private const int SecondsPerHour = 60 * 60;

    /// <summary>
    /// How long ago a seeded hold was made. The expiry follows from it — a hold lasts
    /// <see cref="SchedulingDefaults.ProposalHoldHours"/> from the moment it is minted — so these
    /// are the only two facts a test has to state, and neither is written against a clock this
    /// suite does not control.
    /// </summary>
    private const int HoursAgoStillOpen = 1;
    private const int HoursAgoAlreadyLapsed = SchedulingDefaults.ProposalHoldHours + 1;

    /// <summary>Older than either, so "the newest hold" has something to beat.</summary>
    private const int HoursAgoOlderStill = SchedulingDefaults.ProposalHoldHours * 3;

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();
    private static readonly Guid OtherLinkId = Guid.CreateVersion7();

    /// <summary>
    /// The route is on the group or it is public, and nothing about the answer would look
    /// different either way. <see cref="AdminBoundaryTests"/> pins what the group does; this pins
    /// that this route is inside it.
    /// </summary>
    [Fact]
    public async Task ListMeetings_WithoutASession_IsUnauthorizedRatherThanAnswering()
    {
        using var app = NewApp();

        var response = await app.Client.GetAsync(MeetingsUrl(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The one fact about a meeting the workbench could not show. The invitee has it in their
    /// mail and their calendar; the operator had it only by leaving Kairos and opening the event.
    /// </summary>
    [Fact]
    public async Task ListMeetings_ForABookingWithAConference_CarriesItsJoinUrl()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        (await BookAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Created);
        SeedConference(app);

        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.ConferenceUrl.ShouldBe(JoinUrl);
    }

    /// <summary>
    /// Null, not an empty string. A meeting with nowhere to join is ordinary — a deployment with
    /// no conferencing connected makes nothing else — and the row draws the absence of the action
    /// off this rather than off a string it has to inspect.
    /// </summary>
    [Fact]
    public async Task ListMeetings_ForABookingWithNoConference_CarriesNull()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        (await BookAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.ConferenceUrl.ShouldBeNull();
    }

    /// <summary>
    /// A booking made through a link, read back through the operator's list. The link's name is
    /// the reason this tab exists rather than deferring to the calendar, and the booker's own zone
    /// is what answers "what time did they think this was".
    /// </summary>
    [Fact]
    public async Task ListMeetings_ForABookingMadeThroughALink_CarriesThatLinksNameAndTheBookersZone()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        (await BookAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var answer = await ReadAsync(client);

        answer.TimeZone.ShouldBe(SchedulingDefaults.TimeZoneId);
        var meeting = answer.Meetings.ShouldHaveSingleItem();
        meeting.LinkName.ShouldBe(LinkName);
        meeting.LinkId.ShouldBe(LinkId);
        meeting.ParticipantName.ShouldBe(BookerName);
        meeting.ParticipantEmail.ShouldBe(BookerEmail);
        meeting.ParticipantTimeZone.ShouldBe(BookerZone);
        meeting.DurationMinutes.ShouldBe(DurationMinutes);
        meeting.Status.ShouldBe(nameof(BookingStatus.Confirmed).ToLowerInvariant());
    }

    /// <summary>
    /// Ground rule 14, checked from the surface that reports it. The calendar provider refuses the
    /// write; the booking is still made, still confirmed, and the row says both that it is not in
    /// a calendar and why — which is the difference between a signal and a shrug.
    /// </summary>
    [Fact]
    public async Task ListMeetings_TheCalendarRefusedTheWrite_ReadsNotSyncedWithTheReasonAndTheBookingStands()
    {
        using var app = NewApp();
        var factory = app.OperatorFactory(services =>
        {
            services.RemoveAll<ICalendarWriter>();
            services.AddScoped<ICalendarWriter>(_ => new RefusingCalendar());
        });
        using var client = factory.CreateClient();

        var created = await BookAsync(client);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await DrainFollowUpsAsync(factory);

        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.CalendarSynced.ShouldBeFalse();
        meeting.CalendarError.ShouldBe(Refusal);
        meeting.CalendarProvider.ShouldBe(nameof(IntegrationProvider.Google).ToLowerInvariant());

        // The half that ground rule 14 is actually about: the meeting the domain accepted is
        // intact, and the link it came through is still on it.
        meeting.Status.ShouldBe(nameof(BookingStatus.Confirmed).ToLowerInvariant());
        meeting.LinkName.ShouldBe(LinkName);
        app.Read(context => context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed))
            .ShouldBe(1);
    }

    /// <summary>
    /// The same guarantee at the line a revoked grant reaches. The refusal above throws from the
    /// write; a withdrawn grant never gets that far, the connectivity check refreshing the token
    /// and Google answering <c>invalid_grant</c> there. Both land in the same catch today, which
    /// is why it is pinned: a check answering false instead would write no row, telling the
    /// operator no calendar was ever connected for an account they connected and then revoked.
    /// </summary>
    [Fact]
    public async Task ListMeetings_TheGrantWasRevoked_ReadsNotSyncedWithTheReasonRatherThanAsNeverConnected()
    {
        using var app = NewApp();
        var factory = app.OperatorFactory(services =>
        {
            services.RemoveAll<ICalendarWriter>();
            services.AddScoped<ICalendarWriter>(_ => new RevokedCalendar());
        });
        using var client = factory.CreateClient();

        var created = await BookAsync(client);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await DrainFollowUpsAsync(factory);

        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.CalendarSynced.ShouldBeFalse();

        // The sentence this test exists for. Not synced with no provider and no reason is what a
        // deployment that never connected anything reads as, and it sends the operator to a
        // setting rather than to the reconnect they actually need.
        meeting.CalendarError.ShouldBe(Revocation);
        meeting.CalendarProvider.ShouldBe(nameof(IntegrationProvider.Google).ToLowerInvariant());

        var entry = app.Read(context => context.CalendarEntries.AsNoTracking().Single());
        entry.State.ShouldBe(CalendarSyncState.Failed);
        entry.LastError.ShouldBe(Revocation);

        // Ground rule 14, read from the database rather than from the answer that reported it.
        app.Read(context => context.Bookings.Count(booking => booking.Status == BookingStatus.Confirmed))
            .ShouldBe(1);
    }

    /// <summary>
    /// The ordinary state of a fresh self-hosted Kairos: nothing connected, so nothing was tried.
    /// It has to read as not synced all the same, and must not carry an error — inventing one
    /// would put a fault on every booking of a deployment that never asked to sync.
    /// </summary>
    [Fact]
    public async Task ListMeetings_NoCalendarWasEverConnected_ReadsNotSyncedWithNoReasonGiven()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        await BookAsync(client);

        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.CalendarSynced.ShouldBeFalse();
        meeting.CalendarError.ShouldBeNull();
        meeting.CalendarProvider.ShouldBeNull();
    }

    /// <summary>
    /// The link filter narrows the rows and leaves the options alone. A dropdown that collapsed to
    /// the link just chosen would be a filter with no way back out of itself.
    /// </summary>
    [Fact]
    public async Task ListMeetings_FilteredByLink_NarrowsTheRowsAndKeepsEveryOption()
    {
        using var app = NewApp();
        app.Seed(context =>
        {
            context.Bookings.Add(Meeting(LinkId, FirstDay));
            context.Bookings.Add(Meeting(OtherLinkId, SecondDay));
        });

        using var client = app.CreateOperatorClient();
        var answer = await ReadAsync(client, $"?linkId={OtherLinkId}");

        answer.Meetings.ShouldHaveSingleItem().LinkName.ShouldBe(OtherLinkName);
        answer.Links.Select(link => link.Name).ShouldBe([LinkName, OtherLinkName]);
    }

    /// <summary>
    /// Both ends inclusive, which is what a date range means to somebody typing two days. The
    /// upper bound is the start of the day AFTER `to`, so a meeting at 23:00 on the last day is
    /// inside the range rather than a boundary bug nobody notices for a year.
    /// </summary>
    [Fact]
    public async Task ListMeetings_FilteredByDateRange_IsInclusiveAtBothEnds()
    {
        using var app = NewApp();
        app.Seed(context =>
        {
            context.Bookings.Add(Meeting(LinkId, FirstDay));
            context.Bookings.Add(Meeting(LinkId, SecondDay));
            context.Bookings.Add(Meeting(LinkId, ThirdDay));
        });

        using var client = app.CreateOperatorClient();
        var answer = await ReadAsync(client, $"?from={FirstDay}&to={SecondDay}");

        answer.Meetings.Select(meeting => meeting.Start)
            .ShouldBe([FirstDay + SeededTime, SecondDay + SeededTime]);
    }

    /// <summary>
    /// The two facts the actions column cannot draw itself without.
    /// </summary>
    /// <remarks>
    /// The hold, because a meeting under proposal offers Withdraw where an ordinary one offers
    /// Propose, and the alternative to putting it on the row is a request per row. The slug,
    /// because choosing the time to suggest is a read of that link's own slots — <c>GET
    /// /links/{slug}/slots</c> is keyed on the slug and takes no link id at all, so a row carrying
    /// only <see cref="MeetingResponse.LinkId"/> cannot ask the question.
    /// </remarks>
    [Fact]
    public async Task ListMeetings_ForAMeetingUnderAnOpenProposal_CarriesTheHoldAndTheLinksSlug()
    {
        using var app = NewApp();
        var booking = Meeting(LinkId, FirstDay);
        var proposal = Hold(booking.Id, ProposalState.Open, HoursAgoStillOpen);
        app.Seed(context =>
        {
            context.Bookings.Add(booking);
            context.BookingProposals.Add(proposal);
        });

        using var client = app.CreateOperatorClient();
        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.Slug.ShouldBe(Slug);
        meeting.Proposal.ShouldNotBeNull();
        meeting.Proposal.Id.ShouldBe(proposal.Id);
        meeting.Proposal.Start.ShouldBe(FirstDay + ProposedTime);
        meeting.Proposal.ExpiresAt.ShouldBe(Format(proposal.ExpiresAt));
        meeting.Proposal.State.ShouldBe(nameof(ProposalState.Open).ToLowerInvariant());
    }

    /// <summary>
    /// A meeting nobody has proposed anything for carries no hold, rather than a hold in some
    /// resting state. The row draws Propose off exactly this, and a non-null placeholder would put
    /// Withdraw on every meeting in the list.
    /// </summary>
    [Fact]
    public async Task ListMeetings_ForAMeetingWithNoProposal_CarriesNoneRatherThanAnEmptyOne()
    {
        using var app = NewApp();
        app.Seed(context => context.Bookings.Add(Meeting(LinkId, FirstDay)));

        using var client = app.CreateOperatorClient();

        (await ReadAsync(client)).Meetings.ShouldHaveSingleItem().Proposal.ShouldBeNull();
    }

    /// <summary>
    /// The clock is folded in here exactly as <c>GET /bookings/proposals/{token}</c> folds it, and
    /// for the same reason: a hold whose expiry has passed has stopped holding whether or not the
    /// sweeper has been round to mark it. Nothing sweeps in this test, which is the point —
    /// correctness must not wait on a background service, and a row still offering Withdraw over a
    /// lapsed hold invites the operator to close something that is already closed.
    /// </summary>
    [Fact]
    public async Task ListMeetings_TheHoldLapsedAndNothingSweptIt_ReadsExpiredRatherThanOpen()
    {
        using var app = NewApp();
        var booking = Meeting(LinkId, FirstDay);
        app.Seed(context =>
        {
            context.Bookings.Add(booking);
            context.BookingProposals.Add(Hold(booking.Id, ProposalState.Open, HoursAgoAlreadyLapsed));
        });

        using var client = app.CreateOperatorClient();
        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.Proposal.ShouldNotBeNull();
        meeting.Proposal.State.ShouldBe(nameof(ProposalState.Expired).ToLowerInvariant());

        // Folded in the answer and nowhere else. The row is untouched, so the sweeper still has a
        // lapse to find and an operator still gets told about it.
        app.Read(context => context.BookingProposals.AsNoTracking().Single().State)
            .ShouldBe(ProposalState.Open);
    }

    /// <summary>
    /// One row, so one proposal: the newest. An operator who proposes again after a decline is
    /// asking about the new suggestion, and a row that answered with the old one would draw
    /// "they declined" over a question still being asked.
    /// </summary>
    [Fact]
    public async Task ListMeetings_TheOperatorProposedAgainAfterADecline_CarriesTheNewestHold()
    {
        using var app = NewApp();
        var booking = Meeting(LinkId, FirstDay);
        var declined = Hold(booking.Id, ProposalState.Declined, HoursAgoOlderStill);
        var reopened = Hold(booking.Id, ProposalState.Open, HoursAgoStillOpen);
        app.Seed(context =>
        {
            context.Bookings.Add(booking);
            context.BookingProposals.Add(declined);
            context.BookingProposals.Add(reopened);
        });

        using var client = app.CreateOperatorClient();
        var meeting = (await ReadAsync(client)).Meetings.ShouldHaveSingleItem();

        meeting.Proposal.ShouldNotBeNull();
        meeting.Proposal.Id.ShouldBe(reopened.Id);
    }

    /// <summary>
    /// An inverted range would otherwise answer an empty list, which the page renders as "no
    /// meetings match" — a real answer, for a request that is not one.
    /// </summary>
    [Fact]
    public async Task ListMeetings_ToBeforeFrom_IsABadRequestNamingTheCode()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            MeetingsUrl($"?from={SecondDay}&to={FirstDay}"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        body.GetProperty("code").GetString().ShouldBe(MeetingsAdminEndpoints.RangeInvertedCode);
    }

    private static string MeetingsUrl(string query = "") =>
        $"{ApiVersions.V1}/admin/meetings{query}";

    private static async Task<MeetingsResponse> ReadAsync(HttpClient client, string query = "")
    {
        var answer = await client.GetFromJsonAsync<MeetingsResponse>(
            MeetingsUrl(query), TestContext.Current.CancellationToken);

        answer.ShouldNotBeNull();
        return answer;
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new
            {
                slug = Slug,
                start = Day() + FirstSlotTime,
                durationMinutes = DurationMinutes,
                name = BookerName,
                email = BookerEmail,
                timeZone = BookerZone,
            },
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The booking's creation no longer syncs its calendar inline — a <see cref="FollowUpWorker"/>
    /// does, off the request the invitee waits on. Driven directly rather than waited for, the way
    /// <see cref="ProposalSweeper"/>'s own tests drive <c>SweepAsync</c>: a test that waited on the
    /// worker's timer would be asserting on a clock. <see cref="KairosApp"/> keeps it out of
    /// <c>IHostedService</c> for exactly this reason, so it is resolved as the plain singleton it
    /// is registered as instead — otherwise this call would race the real background pass over
    /// the same row.
    /// </summary>
    /// <param name="factory">
    /// <see cref="OperatorSession.OperatorFactory"/>'s return, never the outer <c>app</c> a test
    /// substituted a calendar on: <c>WithWebHostBuilder</c> hands that substitution a container
    /// of its own, so draining through <c>app.Services</c> would resolve the real
    /// <see cref="ICalendarWriter"/> and leave the test's double never called.
    /// </param>
    private static Task DrainFollowUpsAsync(WebApplicationFactory<KairosDbContext> factory) =>
        factory.Services.GetRequiredService<FollowUpWorker>()
            .DrainAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Computed rather than written down. The create path reads the real clock for minimum
    /// notice, so a literal date would turn this into a suite that passes until it silently stops.
    /// </summary>
    private static string Day() => LocalDatePattern.Iso.Format(
        SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(WellAhead).Next(IsoDayOfWeek.Monday));

    /// <summary>A meeting written straight in, for the tests that are about filtering and not about booking.</summary>
    private static Booking Meeting(Guid linkId, string day)
    {
        var bookingId = Guid.CreateVersion7();
        var start = InstantPattern.ExtendedIso.Parse(day + SeededTime).Value;

        return new Booking
        {
            Id = bookingId,
            LinkId = linkId,
            StartUtc = start.ToUnixTimeSeconds(),
            EndUtc = start.Plus(Duration.FromMinutes(DurationMinutes)).ToUnixTimeSeconds(),
            Status = BookingStatus.Confirmed,
            ManageTokenHash = bookingId.ToString("N"),
            Participants =
            [
                new BookingParticipant
                {
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerEmail,
                    TimeZoneId = BookerZone,
                },
            ],
        };
    }

    /// <summary>
    /// A hold on a seeded meeting, minted <paramref name="hoursAgo"/> ago and lasting the shipped
    /// hold from that moment.
    /// </summary>
    /// <remarks>
    /// Written straight in rather than proposed through the route, because every test using it is
    /// about how a hold READS on the row — the route's own behaviour is
    /// <see cref="ProposalEndpointsTests"/>'s, and going through it would need a configured relay
    /// to say anything about a projection.
    ///
    /// Both instants are taken off the real clock rather than written down, because the endpoint
    /// compares the expiry against the real clock too: a literal would be a hold that is open
    /// today and lapsed the year this suite is still running.
    /// </remarks>
    private static BookingProposal Hold(Guid bookingId, ProposalState state, int hoursAgo)
    {
        var createdAt = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds()
            - ((long)hoursAgo * SecondsPerHour);
        var start = InstantPattern.ExtendedIso.Parse(FirstDay + ProposedTime).Value;

        return new BookingProposal
        {
            Id = Guid.CreateVersion7(),
            BookingId = bookingId,
            StartUtc = start.ToUnixTimeSeconds(),
            EndUtc = start.Plus(Duration.FromMinutes(DurationMinutes)).ToUnixTimeSeconds(),
            ExpiresAt = createdAt + ((long)SchedulingDefaults.ProposalHoldHours * SecondsPerHour),
            CreatedAt = createdAt,
            State = state,
            TokenHash = Guid.CreateVersion7().ToString("N"),
        };
    }

    /// <summary>An instant as the API prints it, for asserting against a seeded second.</summary>
    private static string Format(long unixSeconds) =>
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(unixSeconds));

    private const string JoinUrl = "https://meet.example.com/abc-defg-hij";
    private const string ConferenceExternalId = "abc-defg-hij";

    private static KairosApp NewApp()
    {
        var app = new KairosApp();
        app.Seed(Seed);
        return app;
    }

    /// <summary>The conference a booking is held in, attached after the fact.</summary>
    /// <remarks>
    /// Seeded rather than created through an <see cref="IConferenceCreator"/>: the row is what the
    /// list reads, and driving a provider to produce one would be testing the adapter on the way
    /// past. <see cref="EndpointTests"/> attaches one the same way and for the same reason.
    /// </remarks>
    private static void SeedConference(KairosApp app) =>
        app.Seed(context => context.ConferenceLinks.Add(new ConferenceLink
        {
            BookingId = context.Bookings.Select(booking => booking.Id).Single(),
            Provider = IntegrationProvider.Google,
            ExternalId = ConferenceExternalId,
            JoinUrl = JoinUrl,
        }));

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
            Name = LinkName,
            PatternId = PatternId,
            Active = true,
        });
        context.Links.Add(new Link
        {
            Id = OtherLinkId,
            Slug = OtherSlug,
            Name = OtherLinkName,
            PatternId = PatternId,
            Active = true,
        });

        context.LinkDurations.Add(new LinkDuration { LinkId = LinkId, Minutes = DurationMinutes, SortOrder = 0 });
        context.LinkDurations.Add(new LinkDuration { LinkId = OtherLinkId, Minutes = DurationMinutes, SortOrder = 0 });
    }

    /// <summary>
    /// A calendar that is connected and refuses. This is how the failure is forced: no live grant
    /// is revoked, the provider is simply asked and says no, which is the same thing
    /// <see cref="CalendarSyncService"/> sees when a real grant has been withdrawn.
    /// </summary>
    private sealed class RefusingCalendar : ICalendarWriter
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<PlacedEvent> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<PlacedEvent>(new InvalidOperationException(Refusal));

        /// <summary>
        /// Refuses like the create does, because this calendar refuses. No test here moves a
        /// meeting — the refusal these tests need is the one at creation — but a stub that
        /// accepted a write its own name says it rejects would quietly pass the day one did.
        /// </summary>
        public Task UpdateEventAsync(
            string externalEventId,
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(Refusal));

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A calendar whose grant has been withdrawn. Everything throws, the connectivity check
    /// included: the credential is refreshed first, so the refusal arrives one line earlier than
    /// <see cref="RefusingCalendar"/>'s — the only line a revoked grant ever reaches.
    /// </summary>
    private sealed class RevokedCalendar : ICalendarWriter
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(new InvalidOperationException(Revocation));

        public Task<PlacedEvent> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<PlacedEvent>(new InvalidOperationException(Revocation));

        public Task UpdateEventAsync(
            string externalEventId,
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(Revocation));

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(Revocation));
    }
}
