using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Scheduling;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

public class SchemaTests
{
    private const int MinutesPerHour = 60;

    /// <summary>Minutes from local midnight, which is what the column stores.</summary>
    private const int NineAm = 9 * MinutesPerHour;
    private const int FivePm = 17 * MinutesPerHour;

    /// <summary>
    /// The one date both writers name. Nothing about the calendar decides anything here — what
    /// the race needs is that the two contexts are looking at the same day.
    /// </summary>
    private static readonly DateOnly ContestedDate = new(2026, 9, 14);

    private const string ProposalPatternName = "Proposal pattern";
    private const string ProposalSlug = "proposal";
    private const string ProposalLinkName = "Proposal link";
    private const string ProposalManageHash = "proposal-manage-hash";

    /// <summary>
    /// Two of them, and they have to differ: TokenHash is uniquely indexed, so a second proposal
    /// sharing one would be refused by that index and the test would never reach the filtered one
    /// it is about.
    /// </summary>
    private const string FirstTokenHash = "first-token-hash";
    private const string SecondTokenHash = "second-token-hash";

    private const long ProposalStartUtc = 1_900_000_000;
    private const int ProposalMinutes = 30;
    private const int SecondsPerMinute = 60;
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    /// <summary>Clear of the booking, so nothing here turns on the two intervals overlapping.</summary>
    private const int ProposalOffsetHours = 3;

    private const int ProposalOffsetSeconds = ProposalOffsetHours * SecondsPerHour;
    private const int ProposalHoldSeconds = SchedulingDefaults.ProposalHoldHours * SecondsPerHour;

    /// <summary>The shape a settings row actually holds — a JSON object keyed by field key.</summary>
    private const string SettingsValues = """{"busyCalendars":["operator@example.com"]}""";

    private const string OtherSettingsValues = """{"busyCalendars":[]}""";

    [Fact]
    public void Link_WithSeveralDurations_RoundTripsInSortOrder()
    {
        using var database = new TestDatabase();
        var linkId = Guid.CreateVersion7();

        using (var context = database.CreateContext())
        {
            var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = "Weekdays" };
            context.Patterns.Add(pattern);
            context.Links.Add(new Link
            {
                Id = linkId,
                Slug = "consulting",
                Name = "Consulting",
                PatternId = pattern.Id,
                Durations =
                [
                    new LinkDuration { Minutes = 30, SortOrder = 0 },
                    new LinkDuration { Minutes = 25, SortOrder = 1 }
                ]
            });
            context.SaveChanges();
        }

        using var read = database.CreateContext();
        read.Links
            .AsNoTracking()
            .Include(link => link.Durations)
            .Single()
            .Durations
            .OrderBy(duration => duration.SortOrder)
            .Select(duration => duration.Minutes)
            .ShouldBe([30, 25]);
    }

    [Fact]
    public void SaveChanges_ParticipantWithoutABooking_IsRejectedByTheForeignKey()
    {
        // Proves PRAGMA foreign_keys reached this connection. SQLite ignores foreign keys
        // silently when it is off, so this asserts enforcement rather than declaration.
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        context.BookingParticipants.Add(new BookingParticipant
        {
            Id = Guid.CreateVersion7(),
            BookingId = Guid.CreateVersion7(),   // no such booking
            Role = ParticipantRole.Booker,
            Name = "Nobody",
            Email = "nobody@example.com",
        });

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    /// <summary>
    /// The index asserted as enforcement, not declaration, on the foreign-key test's reasoning:
    /// an index configured and never created refuses nothing. Two rows identical in all four
    /// columns is what two writers who each passed ValidateDay produce.
    /// </summary>
    [Fact]
    public void SaveChanges_WithTheSameWindowOnOneDateTwice_IsRejectedByTheUniqueIndex()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var patternId = Guid.CreateVersion7();
        context.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
        context.SaveChanges();

        context.PatternOverrides.AddRange(
            new PatternOverride
            {
                Id = Guid.CreateVersion7(),
                PatternId = patternId,
                Date = ContestedDate,
                StartMinute = NineAm,
                EndMinute = FivePm,
            },
            new PatternOverride
            {
                Id = Guid.CreateVersion7(),
                PatternId = patternId,
                Date = ContestedDate,
                StartMinute = NineAm,
                EndMinute = FivePm,
            });

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    /// <summary>
    /// The assumption <see cref="Kairos.Api.Shared.ConcurrentWrite"/> stands on: two admin tabs
    /// both read a date's rows before either saves, so the second delete finds nothing and EF
    /// raises rather than writing a merged set. Made silent, the 409 above becomes a lie.
    /// </summary>
    [Fact]
    public void SaveChanges_WhenAnotherWriterAlreadyReplacedTheDate_RaisesConcurrency()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.PatternOverrides.Add(new PatternOverride
            {
                Id = Guid.CreateVersion7(),
                PatternId = patternId,
                Date = ContestedDate,
                StartMinute = NineAm,
                EndMinute = FivePm,
            });
            seed.SaveChanges();
        }

        // Both read before either writes, which is the whole of the race.
        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var firstRows = first.PatternOverrides.Where(row => row.Date == ContestedDate).ToList();
        var secondRows = second.PatternOverrides.Where(row => row.Date == ContestedDate).ToList();

        first.PatternOverrides.RemoveRange(firstRows);
        first.SaveChanges();

        second.PatternOverrides.RemoveRange(secondRows);
        Should.Throw<DbUpdateConcurrencyException>(() => second.SaveChanges());
    }

    /// <summary>
    /// The other half, covering a date holding no rows yet: nothing to delete, so the stale
    /// delete above never fires, and the two windows differ, so the unique index refuses nothing
    /// either. Both writers raise the number they read and the second finds the row moved. Remove
    /// IsConcurrencyToken from the column and the second save succeeds — the state in which two
    /// tabs left a date holding both of their windows.
    /// </summary>
    [Fact]
    public void SaveChanges_WhenTwoWritersBothRaiseTheOverridesVersion_RaisesConcurrency()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.SaveChanges();
        }

        // Both read before either writes, exactly as two requests on one pattern do.
        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var read = first.Patterns.Single(pattern => pattern.Id == patternId);
        var alsoRead = second.Patterns.Single(pattern => pattern.Id == patternId);

        read.OverridesVersion++;
        first.SaveChanges();

        alsoRead.OverridesVersion++;
        Should.Throw<DbUpdateConcurrencyException>(() => second.SaveChanges());
    }

    /// <summary>
    /// Why RulesVersion is raised by an explicit UPDATE rather than carrying IsConcurrencyToken
    /// like the column above. EF builds an update's WHERE clause from the key plus EVERY
    /// concurrency token, not only the changed ones, so a tracked save touching nothing but
    /// RulesVersion is still refused by an override write that moved OverridesVersion on an
    /// unrelated date. Make RulesVersion a token too and that becomes both routes' shipping
    /// behaviour; ReplaceRulesAsync sidesteps it through ExecuteUpdate, which reads no token
    /// metadata.
    /// </summary>
    [Fact]
    public void SaveChanges_WhenARulesWriterSavesUnderAnOverrideWriter_StillCollides()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.SaveChanges();
        }

        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var overrideWriter = first.Patterns.Single(pattern => pattern.Id == patternId);
        var rulesWriter = second.Patterns.Single(pattern => pattern.Id == patternId);

        overrideWriter.OverridesVersion++;
        first.SaveChanges();

        // A different column, and not a token at all. If the WHERE clause held only what this
        // save changed, it would commit.
        rulesWriter.RulesVersion++;
        Should.Throw<DbUpdateConcurrencyException>(() => second.SaveChanges());
    }

    /// <summary>
    /// The other side of the same fact, and the one <see cref="Kairos.Api.Features.Admin.PatternsAdminEndpoints"/>
    /// leans on: the same raise written as a conditional UPDATE goes through, because
    /// ExecuteUpdate is generated from the predicate it is given and nothing else. One row
    /// affected is the week claimed, under an override writer that has already moved the token
    /// this update carries no knowledge of.
    /// </summary>
    [Fact]
    public void ExecuteUpdate_RaisingRulesVersionUnderAnOverrideWriter_ClaimsTheWeekAnyway()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.SaveChanges();
        }

        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var overrideWriter = first.Patterns.Single(pattern => pattern.Id == patternId);
        var seenRulesVersion = second.Patterns.AsNoTracking()
            .Single(pattern => pattern.Id == patternId).RulesVersion;

        overrideWriter.OverridesVersion++;
        first.SaveChanges();

        var claimed = second.Patterns
            .Where(row => row.Id == patternId && row.RulesVersion == seenRulesVersion)
            .ExecuteUpdate(update => update.SetProperty(row => row.RulesVersion, row => row.RulesVersion + 1));

        claimed.ShouldBe(1);
    }

    /// <summary>
    /// The branch the endpoint actually reports on. Zero rows affected is another rules writer
    /// having claimed the week between this one's read and its update — nothing raises, and the
    /// count is the only thing that says so, which is what ReplaceRulesAsync refuses on.
    /// </summary>
    [Fact]
    public void ExecuteUpdate_RaisingARulesVersionAnotherWriterAlreadyRaised_ClaimsNothing()
    {
        using var database = new TestDatabase();
        var patternId = Guid.CreateVersion7();

        using (var seed = database.CreateContext())
        {
            seed.Patterns.Add(new Pattern { Id = patternId, Name = "Weekdays" });
            seed.SaveChanges();
        }

        using var first = database.CreateContext();
        using var second = database.CreateContext();
        var seenRulesVersion = second.Patterns.AsNoTracking()
            .Single(pattern => pattern.Id == patternId).RulesVersion;

        // The other writer, claiming the same week first and by the same means.
        first.Patterns
            .Where(row => row.Id == patternId && row.RulesVersion == seenRulesVersion)
            .ExecuteUpdate(update => update.SetProperty(row => row.RulesVersion, row => row.RulesVersion + 1))
            .ShouldBe(1);

        var claimed = second.Patterns
            .Where(row => row.Id == patternId && row.RulesVersion == seenRulesVersion)
            .ExecuteUpdate(update => update.SetProperty(row => row.RulesVersion, row => row.RulesVersion + 1));

        claimed.ShouldBe(0);
    }

    /// <summary>
    /// One open proposal per meeting, enforced by the database rather than by whoever wrote the
    /// endpoint. Two open holds for one booking means two slots held for one meeting and an
    /// accept race with no good answer.
    /// </summary>
    [Fact]
    public void Schema_ASecondOpenProposalForOneBooking_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var bookingId = SeedBooking(context);
        context.BookingProposals.Add(NewProposal(bookingId, FirstTokenHash));
        context.SaveChanges();

        context.BookingProposals.Add(NewProposal(bookingId, SecondTokenHash));

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    /// <summary>
    /// The filter is what makes the index above liveable: a meeting that has been proposed to
    /// twice and answered twice holds two closed rows, and a third proposal is still allowed.
    /// </summary>
    [Fact]
    public void Schema_SeveralClosedProposalsForOneBooking_AreAllowed()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var bookingId = SeedBooking(context);

        var declined = NewProposal(bookingId, FirstTokenHash);
        declined.State = ProposalState.Declined;
        var expired = NewProposal(bookingId, SecondTokenHash);
        expired.State = ProposalState.Expired;

        context.BookingProposals.AddRange(declined, expired);
        context.SaveChanges();

        context.BookingProposals.Count().ShouldBe(2);
    }

    /// <summary>
    /// One row per provider, said as a constraint rather than as a rule someone has to remember —
    /// the same index <see cref="IntegrationCredential"/> already carries, for the same reason.
    /// </summary>
    [Fact]
    public async Task IntegrationSettings_WithTwoRowsForOneProvider_RefusesTheSecond()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        context.IntegrationSettings.Add(new IntegrationSetting
        {
            Provider = IntegrationProvider.Google,
            Values = SettingsValues,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.IntegrationSettings.Add(new IntegrationSetting
        {
            Provider = IntegrationProvider.Google,
            Values = OtherSettingsValues,
        });

        await Should.ThrowAsync<DbUpdateException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A credential written by a path that never thought about the switch is on. Failure has to be
    /// chosen, never assumed — the same reasoning
    /// <see cref="IntegrationCredential.Health"/> is initialised by.
    /// </summary>
    [Fact]
    public async Task IntegrationCredentials_WithNothingSaidAboutTheSwitch_AreEnabled()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        context.IntegrationCredentials.Add(new IntegrationCredential
        {
            Provider = IntegrationProvider.Google,
            ProtectedPayload = SettingsValues,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await context.IntegrationCredentials.SingleAsync(
            TestContext.Current.CancellationToken);

        stored.Enabled.ShouldBeTrue();
    }

    private static Guid SeedBooking(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = ProposalPatternName };
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            PatternId = pattern.Id,
            Slug = ProposalSlug,
            Name = ProposalLinkName,
            Active = true,
        };
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            LinkId = link.Id,
            StartUtc = ProposalStartUtc,
            EndUtc = ProposalStartUtc + (ProposalMinutes * SecondsPerMinute),
            Status = BookingStatus.Confirmed,
            ManageTokenHash = ProposalManageHash,
            CreatedAt = ProposalStartUtc,
        };

        context.Patterns.Add(pattern);
        context.Links.Add(link);
        context.Bookings.Add(booking);
        context.SaveChanges();

        return booking.Id;
    }

    private static BookingProposal NewProposal(Guid bookingId, string tokenHash) => new()
    {
        Id = Guid.CreateVersion7(),
        BookingId = bookingId,
        StartUtc = ProposalStartUtc + ProposalOffsetSeconds,
        EndUtc = ProposalStartUtc + ProposalOffsetSeconds + (ProposalMinutes * SecondsPerMinute),
        ExpiresAt = ProposalStartUtc + ProposalHoldSeconds,
        CreatedAt = ProposalStartUtc,
        State = ProposalState.Open,
        TokenHash = tokenHash,
    };
}
