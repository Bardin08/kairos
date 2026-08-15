using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Admin;
using Kairos.Api.Integrations;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

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
/// The sync assertions go through the real create path rather than through seeded
/// <see cref="CalendarEntry"/> rows, because the claim being made is about the whole chain — a
/// provider refuses, <see cref="CalendarSyncService"/> records it, the booking survives, and this
/// endpoint reports it. A seeded row would pin the last link of that chain and pass just as
/// happily with the first three broken.
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
        using var client = app.CreateOperatorClient(services =>
        {
            services.RemoveAll<ICalendarWriter>();
            services.AddScoped<ICalendarWriter>(_ => new RefusingCalendar());
        });

        var created = await BookAsync(client);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

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
    /// The same guarantee at the line a revoked grant actually reaches. The refusal above throws
    /// from the write; a withdrawn grant never gets that far, because the connectivity check
    /// refreshes the access token and Google answers <c>invalid_grant</c> there. Both throws land
    /// in the same catch today, which is exactly why this is pinned rather than left to reading: a
    /// check that answered false instead would write no row at all, and the operator would be told
    /// no calendar was ever connected for an account they connected themselves and then revoked.
    /// </summary>
    [Fact]
    public async Task ListMeetings_TheGrantWasRevoked_ReadsNotSyncedWithTheReasonRatherThanAsNeverConnected()
    {
        using var app = NewApp();
        using var client = app.CreateOperatorClient(services =>
        {
            services.RemoveAll<ICalendarWriter>();
            services.AddScoped<ICalendarWriter>(_ => new RevokedCalendar());
        });

        var created = await BookAsync(client);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

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
    /// The ordinary state of a fresh self-hosted Kairos: nothing connected, so nothing was tried
    /// and there is no reason to give. It has to read as not synced all the same — the operator's
    /// calendar does not hold this meeting — and it must not carry an error, because inventing one
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

        public Task<string> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<string>(new InvalidOperationException(Refusal));

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
    /// A calendar whose grant has been withdrawn. Everything throws, including the connectivity
    /// check: the credential is refreshed before any of these can answer, so the refusal arrives
    /// one line earlier than <see cref="RefusingCalendar"/>'s — which is the only line a revoked
    /// grant ever reaches.
    /// </summary>
    private sealed class RevokedCalendar : ICalendarWriter
    {
        public IntegrationProvider Provider => IntegrationProvider.Google;

        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(new InvalidOperationException(Revocation));

        public Task<string> CreateEventAsync(
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<string>(new InvalidOperationException(Revocation));

        public Task UpdateEventAsync(
            string externalEventId,
            CalendarEventRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(Revocation));

        public Task CancelEventAsync(string externalEventId, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(Revocation));
    }
}
