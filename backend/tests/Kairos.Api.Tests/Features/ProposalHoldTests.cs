using System.Net.Http.Json;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using Microsoft.Extensions.DependencyInjection;

using NodaTime;
using NodaTime.Text;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The proposed slot is busy for everyone else while the proposal is open, and stops being busy
/// the moment it lapses. Both conflict readers are exercised, because they are two pieces of code
/// that do not share a line: the generator's busy source decides what the page offers, and
/// BookingWriter's own count under BEGIN IMMEDIATE decides what a write accepts.
/// </summary>
public class ProposalHoldTests
{
    /// <summary>
    /// The world is <see cref="MeetingsFixture"/>'s — same slug, day, duration and nine-to-five
    /// weekdays — because it was copied from here and two copies of one seeded world drift. The
    /// second link the fixture adds is inert: no test here books or proposes through it.
    /// </summary>
    private const string Slug = MeetingsFixture.Slug;
    private const int DurationMinutes = MeetingsFixture.DurationMinutes;
    private const string Day = MeetingsFixture.Day;

    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    private const string HeldTime = "T11:00:00Z";
    private const string ElsewhereTime = "T15:00:00Z";

    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";

    private const long HoursAlreadyLapsed = -1;

    [Fact]
    public async Task GetSlots_ASlotHeldByAnOpenProposal_IsNotOffered()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(app, expiresInHours: SchedulingDefaults.ProposalHoldHours);

        var offered = await ReadSlotsAsync(app);

        offered.ShouldNotContain(Held);
    }

    /// <summary>
    /// The lazy half of the expiry, and the reason the sweeper is not load-bearing: a hold whose
    /// expiry has passed stops holding at that instant, whether or not anything has run to mark
    /// the row Expired.
    /// </summary>
    [Fact]
    public async Task GetSlots_AHoldWhoseExpiryHasPassed_IsOfferedAgain()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(app, expiresInHours: HoursAlreadyLapsed);

        var offered = await ReadSlotsAsync(app);

        offered.ShouldContain(Held);
    }

    /// <summary>
    /// A hold is only as alive as the meeting under it. Nothing in this pull request closes a
    /// proposal when its booking is cancelled, and correctness must not wait: an open, unexpired
    /// hold over a meeting that is off would keep its slot from everyone for the whole window.
    /// </summary>
    [Fact]
    public async Task GetSlots_AHoldOnACancelledMeeting_IsOfferedAgain()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(
            app,
            expiresInHours: SchedulingDefaults.ProposalHoldHours,
            bookingStatus: BookingStatus.Cancelled);

        var offered = await ReadSlotsAsync(app);

        offered.ShouldContain(Held);
    }

    /// <summary>
    /// The invitee said no, and the slot goes back on offer at that moment rather than at the end
    /// of the hold window. The ordinary outcome, and what the State predicate exists for: Declined
    /// leaves the row in place with its expiry in the future, so nothing else stops it holding.
    /// </summary>
    [Fact]
    public async Task GetSlots_AHoldOnADeclinedProposal_IsOfferedAgain()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(
            app,
            expiresInHours: SchedulingDefaults.ProposalHoldHours,
            state: ProposalState.Declined);

        var offered = await ReadSlotsAsync(app);

        offered.ShouldContain(Held);
    }

    /// <summary>
    /// The write path's own answer, and the one that matters: BookingWriter counts clashes inside
    /// BEGIN IMMEDIATE and is the authority on what exists. A page that stopped offering the slot
    /// while the writer still accepted it would be a hold that holds nothing under contention.
    /// </summary>
    [Fact]
    public void TryCreate_ASlotHeldByAnOpenProposal_IsRefused()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(app, expiresInHours: SchedulingDefaults.ProposalHoldHours);

        var result = WriteAsync(app);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.SlotTakenReason);
    }

    [Fact]
    public void TryCreate_AHoldWhoseExpiryHasPassed_IsAccepted()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(app, expiresInHours: HoursAlreadyLapsed);

        WriteAsync(app).Success.ShouldBeTrue();
    }

    /// <summary>
    /// The same question at the writer, which is the authority. Its own test rather than a second
    /// assertion on the one above, because the two readers share no line and a single test would
    /// not say which of them let the dead hold through.
    /// </summary>
    [Fact]
    public void TryCreate_AHoldOnACancelledMeeting_IsAccepted()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(
            app,
            expiresInHours: SchedulingDefaults.ProposalHoldHours,
            bookingStatus: BookingStatus.Cancelled);

        WriteAsync(app).Success.ShouldBeTrue();
    }

    /// <summary>The same question at the writer, which is the authority.</summary>
    [Fact]
    public void TryCreate_AHoldOnADeclinedProposal_IsAccepted()
    {
        using var app = new KairosApp();
        MeetingsFixture.Seed(app);
        SeedProposal(
            app,
            expiresInHours: SchedulingDefaults.ProposalHoldHours,
            state: ProposalState.Declined);

        WriteAsync(app).Success.ShouldBeTrue();
    }

    private static CreateBookingResult WriteAsync(KairosApp app)
    {
        using var scope = app.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<BookingWriter>();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        var link = context.Links.Single(candidate => candidate.Slug == Slug);
        var held = InstantPattern.ExtendedIso.Parse(Held).Value;

        return writer.TryCreate(
            link.Id,
            Slug,
            held,
            Duration.FromMinutes(DurationMinutes),
            new BookerDetails(BookerName, BookerEmail, BookerZone),
            string.Empty,
            SchedulingDefaults.MarginMinutes);
    }

    private static string Held => $"{Day}{HeldTime}";

    private static async Task<IReadOnlyList<string>> ReadSlotsAsync(KairosApp app)
    {
        var offered = await app.Client.GetFromJsonAsync<string[]>(
            $"{ApiVersions.V1}/links/{Slug}/slots?from={Day}&to={Day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        return offered!;
    }

    /// <summary>
    /// A booking elsewhere on the same day — 15:00 against the 11:00 it proposes — carrying one
    /// proposal for <see cref="Held"/>. It sits away from the held slot on purpose: a booking in
    /// the same place would remove the slot whether the hold worked or not. The three parameters
    /// are one per predicate — how long until it lapses, whether the meeting is still on, and
    /// whether the invitee has answered.
    /// </summary>
    private static void SeedProposal(
        KairosApp app,
        long expiresInHours,
        BookingStatus bookingStatus = BookingStatus.Confirmed,
        ProposalState state = ProposalState.Open) => app.Seed(context =>
    {
        var link = context.Links.Single(candidate => candidate.Slug == Slug);
        var elsewhere = InstantPattern.ExtendedIso.Parse($"{Day}{ElsewhereTime}").Value.ToUnixTimeSeconds();
        var held = InstantPattern.ExtendedIso.Parse(Held).Value.ToUnixTimeSeconds();
        var now = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds();

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = elsewhere,
            EndUtc = elsewhere + (DurationMinutes * SecondsPerMinute),
            Status = bookingStatus,
            ManageTokenHash = Guid.CreateVersion7().ToString("N"),
            CreatedAt = now,
            Participants =
            [
                new BookingParticipant
                {
                    Id = Guid.CreateVersion7(),
                    Role = ParticipantRole.Booker,
                    Name = BookerName,
                    Email = BookerEmail,
                    TimeZoneId = BookerZone,
                },
            ],
        };
        context.Bookings.Add(booking);

        context.BookingProposals.Add(new BookingProposal
        {
            Id = Guid.CreateVersion7(),
            BookingId = booking.Id,
            StartUtc = held,
            EndUtc = held + (DurationMinutes * SecondsPerMinute),
            ExpiresAt = now + (expiresInHours * SecondsPerHour),
            CreatedAt = now,
            State = state,
            TokenHash = Guid.CreateVersion7().ToString("N"),
        });
    });
}
