using Kairos.Api.Features.Invites;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The cross-link read: what a row names, and the order the rows arrive in. A list spanning links
/// cannot be read without each row naming the link it belongs to, and it cannot be read at all
/// unless the order is the reader's promise rather than whatever the scan walked into.
/// </summary>
public class InviteReaderTests
{
    private const string ConsultingSlug = "consulting";
    private const string MentorshipSlug = "mentorship";
    private const string ConsultingName = "Consulting call";
    private const string MentorshipName = "Intro chat";
    private const string PatternName = "Weekdays";

    /// <summary>
    /// Two stamps in different seconds, so the CreatedAt clause has something to separate. A
    /// minute apart, which is more than a regenerate ever leaves between a pair.
    /// </summary>
    private const long EarlierSecond = 1_900_000_000;
    private const long LaterSecond = 1_900_000_060;

    /// <summary>
    /// Enough to cross a millisecond boundary, so two v7 ids order on their timestamps rather than
    /// on the random bits under them. Thread.Sleep never returns early — the reasoning
    /// <see cref="Persistence.KeyGenerationTests"/> states for the same sleep.
    /// </summary>
    private const int MillisecondsBetweenIds = 2;

    /// <summary>After both stamps, so nothing here folds to expired.</summary>
    private static readonly Instant Now = Instant.FromUnixTimeSeconds(1_900_001_000);

    /// <summary>
    /// A row names its link, and the newest stamp comes first.
    /// </summary>
    /// <remarks>
    /// The later stamp is given the OLDER id on purpose. Seeded the obvious way the two orderings
    /// agree — the id ascends in step with the stamp — and the CreatedAt clause is then not being
    /// tested at all: measured, a fixture written that way survives having the whole clause
    /// replaced by an ordering on the id. Written this way round they disagree, and only CreatedAt
    /// gives the expected answer.
    /// </remarks>
    [Fact]
    public async Task AllAsync_InvitesOnTwoLinks_ReturnsBothNamingTheirLink()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        var (consulting, mentorship) = SeedLinks(context);
        var (olderId, newerId) = TwoIdsAMillisecondApart();

        Add(context, mentorship, olderId, LaterSecond);
        Add(context, consulting, newerId, EarlierSecond);
        context.SaveChanges();

        var read = await new InviteReader(context, new FixedClock(Now))
            .AllAsync(TestContext.Current.CancellationToken);

        read.Select(invite => invite.LinkName).ShouldBe([MentorshipName, ConsultingName]);
        read[0].LinkId.ShouldBe(mentorship);
    }

    /// <summary>
    /// Newest first across links, rather than newest first within one. The mirror of the case
    /// above, with the stamps on the other link: together they say the answer follows the stamp
    /// and not the link it happens to sit on.
    /// </summary>
    [Fact]
    public async Task AllAsync_TwoLinks_IsNewestFirstRegardlessOfWhichLinkItIsOn()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        var (consulting, mentorship) = SeedLinks(context);
        var (olderId, newerId) = TwoIdsAMillisecondApart();

        Add(context, consulting, olderId, LaterSecond);
        Add(context, mentorship, newerId, EarlierSecond);
        context.SaveChanges();

        var read = await new InviteReader(context, new FixedClock(Now))
            .AllAsync(TestContext.Current.CancellationToken);

        read.Select(invite => invite.LinkName).ShouldBe([ConsultingName, MentorshipName]);
    }

    /// <summary>
    /// The tie the CreatedAt clause cannot separate. Stamps are unix SECONDS, so a regenerate —
    /// one row revoked and its replacement added — reliably produces a pair sharing a second, and
    /// what comes back for a tie is otherwise whatever the scan walked into.
    /// </summary>
    /// <remarks>
    /// The newer id is inserted SECOND, so storage order and id order point opposite ways and only
    /// the id gives the expected answer. Saved one at a time, because a single SaveChanges orders
    /// its own inserts and would prove the sort rather than the tiebreak — the reasoning
    /// <see cref="Persistence.KeyGenerationTests"/> states for its own pair.
    ///
    /// The ids reach SQLite as canonical hyphenated TEXT, which sorts lexicographically and
    /// therefore chronologically once they are v7.
    /// </remarks>
    [Fact]
    public async Task AllAsync_TwoInvitesTiedOnTheSecond_OrdersOnTheIdRatherThanOnStorageOrder()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();

        var (consulting, mentorship) = SeedLinks(context);
        var (olderId, newerId) = TwoIdsAMillisecondApart();

        // The older invite goes on the link that sorts first, so link order and invite order agree
        // on the wrong answer rather than one of them accidentally giving the right one.
        Add(context, consulting, olderId, EarlierSecond);
        context.SaveChanges();
        Add(context, mentorship, newerId, EarlierSecond);
        context.SaveChanges();

        var read = await new InviteReader(context, new FixedClock(Now))
            .AllAsync(TestContext.Current.CancellationToken);

        read.Select(invite => invite.Id).ShouldBe([newerId, olderId]);
    }

    /// <summary>
    /// Two v7 ids that really are ordered. v7 orders on a millisecond timestamp and fills the rest
    /// with random bits rather than a counter, so a pair minted back to back sorts arbitrarily —
    /// which is how a fixture meaning to pin an ordering ends up passing half the time.
    /// </summary>
    private static (Guid Older, Guid Newer) TwoIdsAMillisecondApart()
    {
        var older = Guid.CreateVersion7();
        Thread.Sleep(MillisecondsBetweenIds);
        return (older, Guid.CreateVersion7());
    }

    private static void Add(KairosDbContext context, Guid linkId, Guid id, long createdAt) =>
        context.PersonalInvites.Add(new PersonalInvite
        {
            Id = id,
            LinkId = linkId,
            TokenHash = InviteWriter.NewToken().Hash,
            Prefill = InvitePrefill.Empty,
            CreatedAt = createdAt,
        });

    /// <summary>
    /// Two links on one pattern — Pattern.Name is uniquely indexed, and nothing here reads the
    /// hours anyway.
    /// </summary>
    /// <remarks>
    /// The two link ids are a millisecond apart rather than minted back to back, and consulting is
    /// deliberately the smaller. A tie on CreatedAt is resolved by whatever the plan walked into,
    /// and this query joins Links, so the LINK ids decide that as surely as the invite rowids do —
    /// left random, the tied case above passed roughly half the time with the tiebreak deleted
    /// (measured: 4 of 6 runs). Ordered, every incidental order — invite rowid, link rowid, link
    /// id — points the same wrong way, and only the tiebreak gives the expected answer.
    /// </remarks>
    private static (Guid Consulting, Guid Mentorship) SeedLinks(KairosDbContext context)
    {
        var (consultingId, mentorshipId) = TwoIdsAMillisecondApart();

        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = PatternName };
        var consulting = new Link
        {
            Id = consultingId,
            Slug = ConsultingSlug,
            Name = ConsultingName,
            PatternId = pattern.Id,
        };
        var mentorship = new Link
        {
            Id = mentorshipId,
            Slug = MentorshipSlug,
            Name = MentorshipName,
            PatternId = pattern.Id,
        };

        context.Patterns.Add(pattern);
        context.Links.AddRange(consulting, mentorship);
        context.SaveChanges();
        return (consulting.Id, mentorship.Id);
    }
}
