using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;
using Kairos.Api.Shared;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Availability;

/// <summary>
/// The composition, and the four ways it refuses to answer. Everything the generator itself does
/// is Task 5's; what is pinned here is which row feeds which argument, and that a booking page
/// never reaches the generator with input it cannot survive.
/// </summary>
public class SlotQueryTests
{
    private const string ConsultingSlug = "consulting";
    private const string MentorshipSlug = "mentorship";
    private const int MondayIso = 1;
    private const int TuesdayIso = 2;
    private const int MinutesPerHour = 60;
    private const int NineAm = 9 * MinutesPerHour;
    private const int Noon = 12 * MinutesPerHour;
    private const int HalfHour = 30;
    private const int Hour = 60;
    private const string TokyoZoneId = "Asia/Tokyo";

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();
    private static readonly LocalDate Monday = new(2026, 9, 14);
    private static readonly LocalDate Tuesday = new(2026, 9, 15);

    /// <summary>Days before the range, so minimum notice never decides a test that is not about it.</summary>
    private static readonly Instant WellBefore = Instant.FromUtc(2026, 9, 1, 0, 0);

    [Fact]
    public async Task GetSlotsAsync_UnknownSlug_ThrowsNotFound()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context)
            .GetSlotsAsync("nobody", null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken));

        thrown.Kind.ShouldBe(DomainErrorKind.NotFound);
    }

    [Fact]
    public async Task GetSlotsAsync_DeactivatedLink_ThrowsNotFound()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, active: false);

        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken));

        thrown.Kind.ShouldBe(DomainErrorKind.NotFound);
    }

    [Fact]
    public async Task GetSlotsAsync_RangeWiderThanTheCap_Throws()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context).GetSlotsAsync(
            ConsultingSlug,
            null,
            Monday,
            Monday.PlusDays(SlotQuery.MaxRangeDays),
            WellBefore,
            TestContext.Current.CancellationToken));

        thrown.Code.ShouldBe(SlotQuery.RangeTooWideCode);
    }

    [Fact]
    public async Task GetSlotsAsync_LinkOffersNoDurations_ThrowsTheNamedCodeWithoutEchoingTheSlug()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, durations: []);

        // Durations.Default is durations[0]. Unguarded this is an ArgumentOutOfRangeException
        // naming "index", on a customer-facing page, with nothing saying what went wrong.
        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken));

        thrown.Code.ShouldBe(SlotQuery.NoDurationsCode);

        // The code is what a client keys on, and it is the whole of what this message may carry:
        // the slug arrived from an anonymous caller, and the rule is that it never comes back.
        thrown.Message.ShouldNotContain(ConsultingSlug);
    }

    [Fact]
    public async Task GetSlotsAsync_DurationTheLinkDoesNotOffer_Throws()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context)
            .GetSlotsAsync(ConsultingSlug, Hour, Monday, Monday, WellBefore, TestContext.Current.CancellationToken));

        thrown.Code.ShouldBe(SlotQuery.DurationNotOfferedCode);
    }

    [Fact]
    public async Task GetSlotsAsync_SeveralDurationsAndNoneRequested_Throws()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, durations: [HalfHour, Hour]);

        var thrown = await Should.ThrowAsync<DomainException>(() => Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken));

        thrown.Code.ShouldBe(SlotQuery.DurationRequiredCode);
    }

    [Fact]
    public async Task GetSlotsAsync_SingleDurationLinkWithABookedHalfHour_OffersTheRestOfTheWindow()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        // On the other link, which is the point: availability is one calendar.
        AddBooking(context, MentorshipId, Instant.FromUtc(2026, 9, 14, 10, 0));

        var results = await Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken);

        // 09:30 ends exactly when the booking starts and is still offered — half-open throughout.
        Instant[] expected =
        [
            Instant.FromUtc(2026, 9, 14, 9, 0),
            Instant.FromUtc(2026, 9, 14, 9, 30),
            Instant.FromUtc(2026, 9, 14, 10, 30),
            Instant.FromUtc(2026, 9, 14, 11, 0),
            Instant.FromUtc(2026, 9, 14, 11, 30),
        ];
        results.Starts.ShouldBe(expected);
    }

    [Fact]
    public async Task GetSlotsAsync_OrganizerEastOfGreenwich_SpendsTheDailyCapOnTheirLocalDay()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, isoDays: [MondayIso, TuesdayIso], dailyCap: 1, timeZoneId: TokyoZoneId);

        // 07:00 Tuesday in Tokyo, which is 22:00 Monday in UTC. Grouped on the UTC date it spends
        // Monday's cap; grouped on the organizer's date — the only one the cap is stated in — it
        // spends Tuesday's. It is outside both availability windows, so only the cap can move.
        AddBooking(context, MentorshipId, Instant.FromUtc(2026, 9, 14, 22, 0));

        var results = await Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Tuesday, WellBefore, TestContext.Current.CancellationToken);

        var zone = DateTimeZoneProviders.Tzdb[TokyoZoneId];
        results.Starts.ShouldNotBeEmpty();
        results.Starts.ShouldAllBe(slot => slot.InZone(zone).Date == Monday);
    }

    [Fact]
    public async Task GetSlotsAsync_DailyCapIsZero_OffersNothingEvenOnADayWithNoBookings()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, dailyCap: 0);

        var results = await Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken);

        results.Starts.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSlotsAsync_CancelledBookingOnACappedDay_DoesNotSpendTheCap()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, dailyCap: 1);

        var cancelledHour = Instant.FromUtc(2026, 9, 14, 10, 0);
        AddBooking(context, MentorshipId, cancelledHour, BookingStatus.Cancelled);

        var results = await Query(context)
            .GetSlotsAsync(ConsultingSlug, null, Monday, Monday, WellBefore, TestContext.Current.CancellationToken);

        // A cancellation frees the day it was on: the cap is untouched, and the hour itself is
        // back on offer. Counting cancelled rows would close a one-a-day link for good.
        results.Starts.ShouldContain(cancelledHour);
    }

    [Fact]
    public async Task GetSlotsAsync_RangeExactlyAtTheCap_IsAnswered()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context);

        // Inclusive, so the widest legal range ends MaxRangeDays - 1 days after it starts. The
        // reject side alone would let the effective cap slip to 30 without a test noticing.
        var results = await Query(context).GetSlotsAsync(
            ConsultingSlug,
            null,
            Monday,
            Monday.PlusDays(SlotQuery.MaxRangeDays - 1),
            WellBefore,
            TestContext.Current.CancellationToken);

        results.Starts.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetSlotsAsync_DurationsSortedAgainstTheirLength_ComeBackInTheOperatorsOrder()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        Seed(context, durations: [Hour, HalfHour]);

        var results = await Query(context)
            .GetSlotsAsync(ConsultingSlug, Hour, Monday, Monday, WellBefore, TestContext.Current.CancellationToken);

        // The booking page lists tabs in this order and a single-duration link takes the first of
        // it, so SortOrder is the answer to "which duration", not decoration.
        results.OfferedDurations.ShouldBe([Hour, HalfHour]);
    }

    private static SlotQuery Query(KairosDbContext context) =>
        new(context, new AvailabilityQuery(context), new SlotGenerator(), [new LocalBusySource(context, SystemClock.Instance)]);

    private static void AddBooking(
        KairosDbContext context, Guid linkId, Instant start, BookingStatus status = BookingStatus.Confirmed)
    {
        context.Bookings.Add(new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = linkId,
            StartUtc = start.ToUnixTimeSeconds(),
            EndUtc = (start + Duration.FromMinutes(HalfHour)).ToUnixTimeSeconds(),
            Status = status,
            ManageTokenHash = BookingWriter.Hash(Guid.CreateVersion7().ToString()),
            CreatedAt = start.ToUnixTimeSeconds(),
        });
        context.SaveChanges();
    }

    private static void Seed(
        KairosDbContext context,
        int[]? isoDays = null,
        int[]? durations = null,
        int? dailyCap = null,
        string timeZoneId = SchedulingDefaults.TimeZoneId,
        bool active = true)
    {
        var settings = context.Settings.Single(row => row.Id == Settings.SingletonId);
        settings.TimeZoneId = timeZoneId;

        context.Patterns.Add(new Pattern { Id = PatternId, Name = "Weekdays" });
        foreach (var isoDay in isoDays ?? [MondayIso])
        {
            context.PatternRules.Add(new PatternRule
            {
                Id = Guid.CreateVersion7(),
                PatternId = PatternId,
                IsoDayOfWeek = isoDay,
                StartMinute = NineAm,
                EndMinute = Noon,
            });
        }

        // Zero margin and zero notice on both links, so the only thing that can remove a slot in
        // these tests is the thing the test is about.
        context.Links.AddRange(
            new Link
            {
                Id = ConsultingId,
                Slug = ConsultingSlug,
                Name = "Consulting",
                PatternId = PatternId,
                Active = active,
                MarginMinutes = 0,
                MinNoticeMinutes = 0,
                DailyCap = dailyCap,
                StartIncrementMinutes = HalfHour,
            },
            new Link
            {
                Id = MentorshipId,
                Slug = MentorshipSlug,
                Name = "Mentorship",
                PatternId = PatternId,
                MarginMinutes = 0,
            });

        // SortOrder follows the seeded array's order, not the length, so a test can seed a link
        // whose operator-chosen order disagrees with ascending minutes.
        var offered = durations ?? [HalfHour];
        for (var position = 0; position < offered.Length; position++)
        {
            context.LinkDurations.Add(new LinkDuration
            {
                LinkId = ConsultingId,
                Minutes = offered[position],
                SortOrder = position,
            });
        }

        context.SaveChanges();
    }
}
