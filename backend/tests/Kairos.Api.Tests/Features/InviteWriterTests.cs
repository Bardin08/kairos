using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Invites;
using Kairos.Api.Persistence.Entities;

using NodaTime;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The fold, which is the only place an invite's state is decided. Every surface draws off this
/// answer, so a second copy of it would drift into one page offering a URL another has given up
/// on — the reasoning <see cref="ProposalWriter.StateAt"/> carries for holds.
/// </summary>
public class InviteWriterTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 14, 10, 0);
    private static readonly long NowSeconds = Now.ToUnixTimeSeconds();
    private const long OneSecond = 1;

    /// <summary>Thirty-two bytes rendered as hex, which is what the URL carries.</summary>
    private const int TokenHexLength = 64;

    /// <summary>
    /// The column stores a SHA-256 hex digest, but nothing here depends on that, and a readable
    /// string keeps a failure about the fold rather than about the shape of a hash — which is
    /// what <see cref="Persistence.PersonalInviteSchemaTests"/> settled on for the same reason.
    /// </summary>
    private const string UnspentTokenHash = "unspent-token-hash";

    /// <summary>Nothing on the paths below reads the link, so an id nobody seeded is enough.</summary>
    private static readonly Guid SomeLinkId = Guid.CreateVersion7();

    /// <summary>An operator who filled nothing in, which is what makes these tests about the deadline alone.</summary>
    private static readonly Dictionary<string, string> NoPrefill = [];

    [Fact]
    public void StateAt_NothingSetAndNoDeadline_IsOpen()
    {
        InviteWriter.StateAt(Unspent(), Now).ShouldBe(InviteState.Open);
    }

    [Fact]
    public void StateAt_ADeadlineOneSecondInThePast_IsExpired()
    {
        var invite = Unspent();
        invite.ExpiresAt = NowSeconds - OneSecond;

        InviteWriter.StateAt(invite, Now).ShouldBe(InviteState.Expired);
    }

    /// <summary>An invite is good up to and including its deadline: it lapses once that instant is past, not at it.</summary>
    [Fact]
    public void StateAt_ADeadlineExactlyNow_IsStillOpen()
    {
        var invite = Unspent();
        invite.ExpiresAt = NowSeconds;

        InviteWriter.StateAt(invite, Now).ShouldBe(InviteState.Open);
    }

    /// <summary>
    /// Precedence, and it is not cosmetic: a spent invite that also lapsed reads as Consumed, so
    /// the operator sees that it was used rather than that it timed out.
    /// </summary>
    [Fact]
    public void StateAt_ConsumedAndExpired_IsConsumed()
    {
        var invite = Unspent();
        invite.ConsumedAt = NowSeconds - OneSecond;
        invite.ExpiresAt = NowSeconds - OneSecond;

        InviteWriter.StateAt(invite, Now).ShouldBe(InviteState.Consumed);
    }

    /// <summary>
    /// Consumed outranks revoked, and this is the case that decided the order. Somebody booked
    /// through this invite; that is the thing that happened to it, and a later revocation cannot
    /// unhappen it. Showing Revoked here would tell the operator the invite went unused.
    /// </summary>
    [Fact]
    public void StateAt_ConsumedAndThenRevoked_IsConsumed()
    {
        var invite = Unspent();
        invite.ConsumedAt = NowSeconds - OneSecond;
        invite.RevokedAt = NowSeconds;

        InviteWriter.StateAt(invite, Now).ShouldBe(InviteState.Consumed);
    }

    [Fact]
    public void StateAt_RevokedBeforeAnybodyUsedIt_IsRevoked()
    {
        var invite = Unspent();
        invite.RevokedAt = NowSeconds - OneSecond;

        InviteWriter.StateAt(invite, Now).ShouldBe(InviteState.Revoked);
    }

    /// <summary>
    /// The mirror of <see cref="StateAt_ADeadlineExactlyNow_IsStillOpen"/>, and the two boundaries
    /// have to agree on the same second: were issuance stricter than the fold, an operator naming
    /// this instant would either be refused an invite the fold calls open, or handed one that
    /// reads as expired the moment it exists.
    /// </summary>
    [Fact]
    public void Issue_ADeadlineExactlyNow_IsAccepted()
    {
        // A real context, and no link row: Add never saves, so the foreign key on LinkId is never
        // checked and there is nothing here for a seeded link to change.
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var writer = new InviteWriter(context, new FixedClock(Now));

        writer.Issue(SomeLinkId, NoPrefill, Now).Refusal.ShouldBeNull();
    }

    /// <summary>
    /// The paired refusal. An invite born expired is a bug only issuance can catch: afterwards it
    /// is indistinguishable from one that lapsed honestly.
    /// </summary>
    [Fact]
    public void Issue_ADeadlineOneSecondInThePast_IsRefused()
    {
        var writer = new InviteWriter(context: null!, new FixedClock(Now));

        var result = writer.Issue(SomeLinkId, NoPrefill, Now - Duration.FromSeconds(OneSecond));

        result.Refusal.ShouldBe(IssueRefusal.DeadlinePast);
    }

    /// <summary>
    /// The other half of the same decision: revoking something already spent writes nothing, so
    /// the pair the fold has to disambiguate is one the writer will not create in the first place.
    /// </summary>
    [Fact]
    public void Revoke_AnInviteSomebodyAlreadyUsed_WritesNothing()
    {
        var invite = Unspent();
        invite.ConsumedAt = NowSeconds - OneSecond;

        new InviteWriter(context: null!, new FixedClock(Now)).Revoke(invite);

        invite.RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// RevokedAt records when the invite was first taken back. A second click is the operator
    /// clicking twice, not a second revocation, and moving the timestamp would rewrite when it
    /// stopped being spendable.
    /// </summary>
    [Fact]
    public void Revoke_TwiceOverTheSameInvite_KeepsTheFirstTimestamp()
    {
        var invite = Unspent();

        new InviteWriter(context: null!, new FixedClock(Now)).Revoke(invite);
        new InviteWriter(context: null!, new FixedClock(Now + Duration.FromSeconds(OneSecond))).Revoke(invite);

        invite.RevokedAt.ShouldBe(NowSeconds);
    }

    [Fact]
    public void NewToken_IsThirtyTwoBytesOfHexAndItsHashMatches()
    {
        var (token, hash) = InviteWriter.NewToken();

        token.Length.ShouldBe(TokenHexLength);
        token.ShouldBe(token.ToLowerInvariant());
        hash.ShouldBe(BookingWriter.Hash(token));
    }

    /// <summary>
    /// A result that reaches a log line must not take the token with it: whoever holds one can
    /// book as the invitee. The identifier survives, because a redacted result nobody can trace
    /// back to a row is the other way to make the log line useless.
    /// </summary>
    [Fact]
    public void ToString_AnIssuedResult_RedactsTheTokenAndKeepsTheId()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();

        var result = new InviteWriter(context, new FixedClock(Now))
            .Issue(SomeLinkId, NoPrefill, expiresAt: null);

        var printed = result.ToString();

        printed.ShouldNotContain(result.Token!);
        printed.ShouldContain(result.Invite!.Id.ToString());
    }

    [Fact]
    public void NewToken_TwiceInARow_DiffersEveryTime()
    {
        InviteWriter.NewToken().Token.ShouldNotBe(InviteWriter.NewToken().Token);
    }

    private static PersonalInvite Unspent() => new()
    {
        TokenHash = UnspentTokenHash,
        Prefill = InvitePrefill.Empty,
        CreatedAt = NowSeconds,
    };
}
