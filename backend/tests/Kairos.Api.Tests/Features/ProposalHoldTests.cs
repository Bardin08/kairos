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
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string PatternName = "Weekdays";
    private const int DurationMinutes = 30;
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    /// <summary>Far enough ahead that the real clock's minimum notice never decides a test.</summary>
    private const string Day = "2030-09-09";
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
        SeedLink(app);
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
        SeedLink(app);
        SeedProposal(app, expiresInHours: HoursAlreadyLapsed);

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
        SeedLink(app);
        SeedProposal(app, expiresInHours: SchedulingDefaults.ProposalHoldHours);

        var result = WriteAsync(app);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.SlotTakenReason);
    }

    [Fact]
    public void TryCreate_AHoldWhoseExpiryHasPassed_IsAccepted()
    {
        using var app = new KairosApp();
        SeedLink(app);
        SeedProposal(app, expiresInHours: HoursAlreadyLapsed);

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

    private static void SeedLink(KairosApp app) => app.Seed(context =>
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        context.Patterns.Add(pattern);

        for (var day = IsoDayOfWeek.Monday; day <= IsoDayOfWeek.Sunday; day++)
        {
            context.PatternRules.Add(new PatternRule
            {
                Id = Guid.CreateVersion7(),
                PatternId = pattern.Id,
                IsoDayOfWeek = (int)day,
                StartMinute = NineAm,
                EndMinute = FivePm,
            });
        }

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            PatternId = pattern.Id,
            Slug = Slug,
            Name = LinkName,
            Active = true,
        };
        context.Links.Add(link);
        context.LinkDurations.Add(new LinkDuration
        {
            LinkId = link.Id,
            Minutes = DurationMinutes,
            SortOrder = 0,
        });
    });

    /// <summary>
    /// A booking on an unrelated day carrying an open proposal for <see cref="Held"/>. The booking
    /// is elsewhere on purpose: what is being asserted is that the HOLD removes the slot, and a
    /// booking in the same place would remove it whether the hold worked or not.
    /// </summary>
    private static void SeedProposal(KairosApp app, long expiresInHours) => app.Seed(context =>
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
            Status = BookingStatus.Confirmed,
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
            State = ProposalState.Open,
            TokenHash = Guid.CreateVersion7().ToString("N"),
        });
    });
}
