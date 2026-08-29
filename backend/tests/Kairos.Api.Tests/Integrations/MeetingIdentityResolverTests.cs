using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

using Shouldly;

namespace Kairos.Api.Tests.Integrations;

/// <summary>
/// Which identity a notice quotes, and the one rule that decides it.
/// </summary>
/// <remarks>
/// Both halves move together on purpose: a UID the client already holds, announced under an
/// organizer it has never seen own that meeting, is the combination nothing documents. The
/// fallback gives up both or neither.
/// </remarks>
public class MeetingIdentityResolverTests
{
    private const string From = "Kairos <booking@kairos.example>";
    private const string FromAddress = "booking@kairos.example";
    private const string ConnectedAccount = "organizer@gmail.example";
    private const string ProviderUid = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0@google.com";
    private const string EventId = "e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0";
    private const long MeetingStartUtc = 1_788_000_000;
    private const long MeetingEndUtc = 1_788_003_600;

    [Fact]
    public async Task ResolveAsync_AProviderPlacedTheEvent_QuotesItsUidAndItsAccount()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var bookingId = Seed(context, ProviderUid, ConnectedAccount);

        var identity = await Resolver(context)
            .ResolveAsync(bookingId, TestContext.Current.CancellationToken);

        identity.Uid.ShouldBe(ProviderUid);
        identity.OrganizerEmail.ShouldBe(ConnectedAccount);
        identity.SentBy.ShouldBe(FromAddress);
    }

    [Fact]
    public async Task ResolveAsync_NoProviderPlacedIt_QuotesTheBookingIdAndTheMailFrom()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var bookingId = Seed(context, calendarUid: null, accountLabel: null);

        var identity = await Resolver(context)
            .ResolveAsync(bookingId, TestContext.Current.CancellationToken);

        identity.Uid.ShouldBe(bookingId.ToString());
        identity.OrganizerEmail.ShouldBe(FromAddress);
        identity.SentBy.ShouldBeNull();
    }

    /// <summary>
    /// The event is in a calendar whose account Kairos cannot name — a credential row that lost
    /// its label. Quoting the provider's UID under the mail From would assert an organizer no
    /// client has seen for that meeting, so both halves fall back together.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_TheAccountIsUnknown_FallsBackToBothHalves()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var bookingId = Seed(context, ProviderUid, accountLabel: null);

        var identity = await Resolver(context)
            .ResolveAsync(bookingId, TestContext.Current.CancellationToken);

        identity.Uid.ShouldBe(bookingId.ToString());
        identity.OrganizerEmail.ShouldBe(FromAddress);
        identity.SentBy.ShouldBeNull();
    }

    private static MeetingIdentityResolver Resolver(KairosDbContext context) =>
        new(context, Options.Create(new EmailOptions { From = From }));

    private static Guid Seed(KairosDbContext context, string? calendarUid, string? accountLabel)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);

        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            Slug = "consulting",
            Name = "Consulting",
            PatternId = pattern.Id,
        };
        context.Links.Add(link);

        var bookingId = Guid.CreateVersion7();
        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            LinkId = link.Id,
            StartUtc = MeetingStartUtc,
            EndUtc = MeetingEndUtc,
            Status = BookingStatus.Confirmed,
            ManageTokenHash = "hash",
            CreatedAt = MeetingStartUtc,
        });

        if (calendarUid is not null)
        {
            context.CalendarEntries.Add(new CalendarEntry
            {
                BookingId = bookingId,
                Provider = IntegrationProvider.Google,
                ExternalEventId = EventId,
                CalendarUid = calendarUid,
                State = CalendarSyncState.Synced,
                UpdatedAt = MeetingStartUtc,
            });
        }

        if (accountLabel is not null)
        {
            context.IntegrationCredentials.Add(new IntegrationCredential
            {
                Id = Guid.CreateVersion7(),
                Provider = IntegrationProvider.Google,
                ProtectedPayload = "ciphertext",
                AccountLabel = accountLabel,
                ConnectedAt = MeetingStartUtc,
                UpdatedAt = MeetingStartUtc,
            });
        }

        context.SaveChanges();
        return bookingId;
    }
}
