using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The writer's rules and its failure paths. The conflict rule is pinned from both sides — the
/// margin can be the larger one on either link — and every failure has to leave the caller's
/// transaction, the change tracker and the log exactly as it found them.
/// </summary>
public class BookingWriterTests
{
    private const int NoMargin = 0;
    private const int LargerMarginMinutes = 30;
    private const int SmallerMarginMinutes = 20;
    private const int SlotMinutes = 30;
    private const int MeetingMinutes = 60;
    private const int CallersMargin = 999;

    private static readonly Guid ConsultingId = Guid.CreateVersion7();
    private static readonly Guid MentorshipId = Guid.CreateVersion7();
    private static readonly Instant MeetingStart = Instant.FromUtc(2026, 9, 14, 10, 0);
    private static readonly Duration Meeting = Duration.FromMinutes(MeetingMinutes);
    private static readonly Duration SlotDuration = Duration.FromMinutes(SlotMinutes);
    private static readonly BookerDetails Booker = new("Ada", "ada@example.com", "Europe/Warsaw");

    [Fact]
    public void TryCreate_QueryingLinkHasTheLargerMargin_BlocksTheSmallerMarginMeeting()
    {
        // The mirror of the case in BookingConcurrencyTests: there the booked meeting owns the
        // larger margin, here the link doing the asking does. Without both, an implementation
        // that reads only one side of the MAX passes.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, SmallerMarginMinutes);
        var writer = new BookingWriter(context, SystemClock.Instance);

        writer.TryCreate(ConsultingId, MeetingStart, Meeting, Booker, string.Empty, SmallerMarginMinutes)
            .Success.ShouldBeTrue();

        // The meeting asks for twenty minutes; the caller asks for thirty, so thirty wins.
        var insideTheLargerMargin = MeetingStart + Meeting + Duration.FromMinutes(SmallerMarginMinutes);
        var atTheLargerMargin = MeetingStart + Meeting + Duration.FromMinutes(LargerMarginMinutes);

        writer.TryCreate(MentorshipId, insideTheLargerMargin, SlotDuration, Booker, string.Empty, LargerMarginMinutes)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);
        writer.TryCreate(MentorshipId, atTheLargerMargin, SlotDuration, Booker, string.Empty, LargerMarginMinutes)
            .Success.ShouldBeTrue();
    }

    [Fact]
    public void TryCreate_SlotEndsExactlyWhenTheBlockedWindowBegins_IsAccepted()
    {
        // The left edge. SlotGenerator offers a slot that ends the instant a busy block's padding
        // starts, so the writer has to accept the same one, or the page offers what the write
        // refuses. The minute before it must still be refused, or this asserts nothing.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, LargerMarginMinutes);
        var writer = new BookingWriter(context, SystemClock.Instance);

        writer.TryCreate(ConsultingId, MeetingStart, Meeting, Booker, string.Empty, LargerMarginMinutes)
            .Success.ShouldBeTrue();

        var blockedFrom = MeetingStart - Duration.FromMinutes(LargerMarginMinutes);
        writer.TryCreate(MentorshipId, blockedFrom - SlotDuration, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();
        writer.TryCreate(
                MentorshipId,
                blockedFrom - SlotDuration + Duration.FromMinutes(1),
                SlotDuration,
                Booker,
                string.Empty,
                NoMargin)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);
    }

    [Fact]
    public void TryCreate_TheClashingBookingIsCancelled_FreesTheSlot()
    {
        // Cancelling has to free the time everywhere, not only on the page that drew it. The
        // status filter in the clash query is the only thing that makes that true on the write.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);
        var writer = new BookingWriter(context, SystemClock.Instance);

        writer.TryCreate(ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();
        writer.TryCreate(MentorshipId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);

        var booked = context.Bookings.Single();
        booked.Status = BookingStatus.Cancelled;
        booked.CancelledAt = SystemClock.Instance.GetCurrentInstant().ToUnixTimeSeconds();
        context.SaveChanges();

        writer.TryCreate(MentorshipId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();
    }

    [Fact]
    public void TryCreate_TheUniqueIndexFiresBehindTheClashCheck_ReportsSlotTaken()
    {
        // The backstop, reached deliberately: two zero-length bookings at one instant do not
        // overlap under a half-open comparison, so the clash query waves the second one through
        // and only IX_Bookings_StartUtc stops it. It is the path a lost race takes, and the one
        // that has to come back as a refusal rather than as a 500.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);
        var writer = new BookingWriter(context, SystemClock.Instance);

        writer.TryCreate(ConsultingId, MeetingStart, Duration.Zero, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();

        var result = writer.TryCreate(
            MentorshipId, MeetingStart, Duration.Zero, Booker, string.Empty, NoMargin);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldBe(BookingWriter.SlotTakenReason);
        context.Bookings.AsNoTracking().Count().ShouldBe(1);
    }

    [Fact]
    public void TryCreate_SettingsSingletonIsMissing_StillRefusesAClash()
    {
        // Joined the wrong way, a missing settings row drops every booking out of the count and
        // the answer becomes "nothing clashes" — one absent row silently disabling the only check
        // this class performs. Inheriting no margin is the right failure; accepting everything
        // is not.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);

        var inheriting = context.Links.Single(link => link.Id == MentorshipId);
        inheriting.MarginMinutes = null;
        context.SaveChanges();

        var writer = new BookingWriter(context, SystemClock.Instance);
        writer.TryCreate(MentorshipId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin)
            .Success.ShouldBeTrue();

        context.Database.ExecuteSqlRaw("DELETE FROM Settings;");

        // Overlapping rather than identical: an identical start would be stopped by the unique
        // index whatever the query returned, and the backstop would hide the hole being tested.
        var overlapping = MeetingStart + Duration.FromMinutes(SlotMinutes / 2);
        writer.TryCreate(ConsultingId, overlapping, SlotDuration, Booker, string.Empty, NoMargin)
            .Reason.ShouldBe(BookingWriter.SlotTakenReason);
    }

    [Fact]
    public void TryCreate_CallerAlreadyHoldsATransaction_LeavesTheCallersWorkIntact()
    {
        // A reschedule is a cancel and a create in one transaction, so this is the shape Part B
        // will arrive in. Rolling back a transaction this call never opened would throw the
        // caller's uncommitted work away while reporting only the failure that caused it.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        SqlitePragmas.Apply(connection);
        Execute(connection, $"BEGIN IMMEDIATE; UPDATE Settings SET MarginMinutes = {CallersMargin};");

        var writer = new BookingWriter(context, SystemClock.Instance);
        Should.Throw<SqliteException>(() =>
            writer.TryCreate(ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin));

        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT MarginMinutes FROM Settings WHERE Id = {Settings.SingletonId};";
        read.ExecuteScalar().ShouldBe((long)CallersMargin);

        Execute(connection, "ROLLBACK;");
    }

    [Fact]
    public void TryCreate_LinkDoesNotExist_ThrowsAndLeavesNothingTracked()
    {
        // A foreign key violation is not a taken slot. Reported as one it becomes a 409 telling
        // the booker to pick another time for a request no time would have fixed, and the real
        // cause reaches no log. Left in the change tracker, the refused insert rides along on
        // whatever the caller saves next.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);
        var writer = new BookingWriter(context, SystemClock.Instance);

        Should.Throw<DbUpdateException>(() =>
            writer.TryCreate(Guid.CreateVersion7(), MeetingStart, SlotDuration, Booker, string.Empty, NoMargin));

        context.ChangeTracker.Entries().Count(entry => entry.State == EntityState.Added).ShouldBe(0);
        context.SaveChanges();
        context.Bookings.AsNoTracking().Count().ShouldBe(0);
    }

    [Fact]
    public void TryCreate_ManageTokenArrivesUpperCased_StillMatchesTheStoredHash()
    {
        // The stored value is a hash, and the token is hashed case-insensitively, so a manage
        // link that came back shouting still finds its row instead of a bare 404.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);

        var result = new BookingWriter(context, SystemClock.Instance)
            .TryCreate(ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin);

        context.Bookings.AsNoTracking().Single()
            .ManageTokenHash.ShouldBe(BookingWriter.Hash(result.ManageToken!.ToUpperInvariant()));
    }

    [Fact]
    public void ToString_ResultCarriesAManageToken_DoesNotPrintIt()
    {
        // The token is a bearer capability: whoever reads it out of a log can cancel the meeting.
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        Seed(context, NoMargin);

        var result = new BookingWriter(context, SystemClock.Instance)
            .TryCreate(ConsultingId, MeetingStart, SlotDuration, Booker, string.Empty, NoMargin);

        var printed = result.ToString();
        printed.ShouldNotContain(result.ManageToken!);
        printed.ShouldContain(result.Id.ToString());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Seed(KairosDbContext context, int consultingMarginMinutes)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
        context.Patterns.Add(pattern);
        context.Links.AddRange(
            new Link
            {
                Id = ConsultingId,
                Slug = "consulting",
                Name = "Consulting",
                PatternId = pattern.Id,
                MarginMinutes = consultingMarginMinutes,
            },
            new Link
            {
                Id = MentorshipId,
                Slug = "mentorship",
                Name = "Mentorship",
                PatternId = pattern.Id,
                MarginMinutes = NoMargin,
            });
        context.SaveChanges();
    }
}
