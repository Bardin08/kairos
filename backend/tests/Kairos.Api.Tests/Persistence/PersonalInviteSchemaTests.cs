using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Persistence;

/// <summary>
/// What the invites table guarantees on its own, rather than by every writer remembering to.
/// </summary>
public class PersonalInviteSchemaTests
{
    private const string InvitePatternName = "Invite pattern";
    private const string FirstLinkSlug = "consulting";
    private const string FirstLinkName = "Consulting";
    private const string SecondLinkSlug = "coaching";
    private const string SecondLinkName = "Coaching";

    /// <summary>
    /// The column stores a SHA-256 hex digest, but nothing in the schema says so, and a readable
    /// string keeps the failure about the index rather than about the shape of a hash.
    /// </summary>
    private const string SharedTokenHash = "shared-token-hash";

    /// <summary>An invite the operator prefilled nothing on, which is still a valid invite.</summary>
    private const string EmptyPrefill = "{}";

    private const long InviteCreatedAt = 1_900_000_000;

    /// <summary>
    /// The hash is the invite's identity across the whole table, and the two rows sit on different
    /// links because that is the collision that matters: the public route is handed a hash and no
    /// link, so a hash shared by two links resolves to two invites with nothing to choose between
    /// them. Two links is also what keeps this on the unique index and off the (LinkId, CreatedAt)
    /// one — on a single link the pair would collide too, and either index could be the refuser.
    /// Asserted as enforcement rather than declaration: an index configured and never created
    /// refuses nothing.
    /// </summary>
    [Fact]
    public void Schema_TheSameTokenHashOnASecondLink_IsRefused()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var patternId = SeedPattern(context);
        var firstLinkId = SeedLink(context, patternId, FirstLinkSlug, FirstLinkName);
        var secondLinkId = SeedLink(context, patternId, SecondLinkSlug, SecondLinkName);

        context.PersonalInvites.Add(NewInvite(firstLinkId));
        context.SaveChanges();

        context.PersonalInvites.Add(NewInvite(secondLinkId));

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    /// <summary>One for both links, because Pattern.Name is uniquely indexed and two links sharing
    /// a pattern is the ordinary arrangement anyway.</summary>
    private static Guid SeedPattern(KairosDbContext context)
    {
        var pattern = new Pattern { Id = Guid.CreateVersion7(), Name = InvitePatternName };

        context.Patterns.Add(pattern);
        context.SaveChanges();

        return pattern.Id;
    }

    private static Guid SeedLink(KairosDbContext context, Guid patternId, string slug, string name)
    {
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            PatternId = patternId,
            Slug = slug,
            Name = name,
        };

        context.Links.Add(link);
        context.SaveChanges();

        return link.Id;
    }

    private static PersonalInvite NewInvite(Guid linkId) => new()
    {
        Id = Guid.CreateVersion7(),
        LinkId = linkId,
        TokenHash = SharedTokenHash,
        Prefill = EmptyPrefill,
        CreatedAt = InviteCreatedAt,
    };
}
