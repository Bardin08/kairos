using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kairos.Api.Features.Invites;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The route the booking page calls before it draws a form. Anonymous, keyed by a token, and it
/// answers the state rather than refusing: a stale invite is still a link somebody can book
/// through, so the page draws the ordinary form and a notice. Only a token no invite has is a 404.
/// </summary>
public class InviteEndpointTests
{
    private const string Slug = "consulting";
    private const string LinkName = "Consulting call";
    private const string PatternName = "Weekdays";

    private const string NameField = "name";
    private const string SomeName = "Ada";

    /// <summary>
    /// A stored key <see cref="InvitePrefill"/> does not list as fillable. It cannot be written
    /// through the operator's route, which refuses one — so it stands for a key a later version
    /// added, or an earlier one wrote, and the row outlived.
    /// </summary>
    private const string UnknownField = "favourite_colour";

    private const string SomeColour = "green";

    /// <summary>The lower-cased <see cref="InviteState"/> names, which is what the page keys on.</summary>
    private const string OpenState = "open";
    private const string ConsumedState = "consumed";
    private const string RevokedState = "revoked";
    private const string ExpiredState = "expired";

    /// <summary>2020-01-01T00:00:00Z — comfortably before any clock this suite runs under.</summary>
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>2099-01-01T00:00:00Z — comfortably after it, so a deadline set here is still ahead.</summary>
    private static readonly DateTimeOffset FarOff = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly long LongAgoSeconds = LongAgo.ToUnixTimeSeconds();
    private static readonly long FarOffSeconds = FarOff.ToUnixTimeSeconds();

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();

    private static readonly string SeededPrefill = InvitePrefill.Serialize(
        new Dictionary<string, string> { [NameField] = SomeName });

    /// <summary>
    /// A bag carrying one key this version knows and one it does not. Both have to come back:
    /// <see cref="InvitePrefill.Read"/> is deliberately more permissive than
    /// <see cref="InvitePrefill.Validate"/>, and nothing else at the HTTP layer pins that.
    /// </summary>
    private static readonly string PrefillWithAKeyThisVersionDoesNotKnow = InvitePrefill.Serialize(
        new Dictionary<string, string> { [NameField] = SomeName, [UnknownField] = SomeColour });

    /// <summary>
    /// Everything the page needs before it draws a seeded form: which link is being booked, that
    /// the invite is still spendable, what to put in the fields, and when it stops working — a
    /// deadline the invitee cannot see is a page that stops working without saying why.
    /// </summary>
    [Fact]
    public async Task Get_AnOpenInvite_AnswersTheSlugThePrefillAndTheDeadline()
    {
        using var app = Seeded();
        var token = Issued(app, SeededPrefill, invite => invite.ExpiresAt = FarOffSeconds);

        var response = await ReadAsync(app, token);

        response.Slug.ShouldBe(Slug);
        response.State.ShouldBe(OpenState);
        response.Prefill[NameField].ShouldBe(SomeName);
        response.ExpiresAt.ShouldBe(FarOff);
    }

    /// <summary>
    /// The decision the page's whole design rests on. Somebody already booked through this invite,
    /// and the answer is still a 200 naming the state: refusing would take a working booking link
    /// away from a person whose only fault is arriving on a URL that has already been spent.
    /// </summary>
    [Fact]
    public async Task Get_AConsumedInvite_AnswersConsumedRatherThanNotFound()
    {
        using var app = Seeded();

        // Written straight onto the row: the consuming UPDATE lives inside BookingWriter's own
        // transaction and does not exist yet.
        var token = Issued(app, SeededPrefill, invite => invite.ConsumedAt = LongAgoSeconds);

        var response = await ReadAsync(app, token);

        response.State.ShouldBe(ConsumedState);
    }

    [Fact]
    public async Task Get_ARevokedInvite_AnswersRevoked()
    {
        using var app = Seeded();
        var token = Issued(app, SeededPrefill, invite => invite.RevokedAt = LongAgoSeconds);

        var response = await ReadAsync(app, token);

        response.State.ShouldBe(RevokedState);
    }

    /// <summary>
    /// Nothing marked it, and nothing ever will: this feature adds no <c>BackgroundService</c>.
    /// Not even <see cref="Kairos.Api.Features.Bookings.ProposalSweeper"/> is what makes a lapse
    /// correct — every reader compares the deadline against the clock itself, and what that service
    /// owns is the noticing, which it spends on a mail to the operator. An invite has nobody to
    /// tell, so the clock is the whole of it.
    /// </summary>
    [Fact]
    public async Task Get_ADeadlineInThePast_AnswersExpiredWithNoJobHavingRun()
    {
        using var app = Seeded();
        var token = Issued(app, SeededPrefill, invite => invite.ExpiresAt = LongAgoSeconds);

        var response = await ReadAsync(app, token);

        response.State.ShouldBe(ExpiredState);
    }

    /// <summary>
    /// The one 404 this route has. The token is well formed and minted the way a real one is; it
    /// is simply not written down anywhere, which is what "no such invite" looks like from outside.
    /// </summary>
    /// <remarks>
    /// The body is asserted and not only the status, because a bare <c>TypedResults.NotFound()</c>
    /// also answers 404 — with nothing in it. No <c>UseStatusCodePages</c> is registered, so a
    /// refusal that skips <see cref="DomainException"/> reaches the page as an empty body, and the
    /// booking page would be the one caller in the frontend with no code to branch on.
    /// </remarks>
    [Fact]
    public async Task Get_ATokenNoInviteHas_IsNotFoundWithACodeToBranchOn()
    {
        using var app = Seeded();
        var (token, _) = InviteWriter.NewToken();

        var response = await app.Client.GetAsync(
            PathFor(token), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        problem.GetProperty(ProblemCode.Key).GetString().ShouldBe(DomainException.NotFoundCode);
    }

    /// <summary>
    /// Reading a prefill is deliberately more permissive than writing one: a stored key this
    /// version does not know is carried through rather than dropped, because the invite is still
    /// spendable and a form that cannot seed one field is not a reason to refuse a booking. Pinned
    /// here because that permissiveness is a decision, and the route is where it is spent.
    /// </summary>
    [Fact]
    public async Task Get_APrefillCarryingAKeyThisVersionDoesNotKnow_AnswersItIntact()
    {
        using var app = Seeded();
        var token = Issued(app, PrefillWithAKeyThisVersionDoesNotKnow);

        var response = await ReadAsync(app, token);

        response.Prefill[UnknownField].ShouldBe(SomeColour);
        response.Prefill[NameField].ShouldBe(SomeName);
    }

    private static string PathFor(string token) => $"{ApiVersions.V1}/invites/{token}";

    /// <summary>
    /// The plain client rather than an operator's, which is the assertion that dies if this route
    /// is ever mapped onto the admin group: the token is the credential here, exactly as it is for
    /// <c>GET /bookings/{token}</c>.
    /// </summary>
    private static async Task<InviteReadResponse> ReadAsync(KairosApp app, string token)
    {
        var response = await app.Client.GetAsync(
            PathFor(token), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<InviteReadResponse>(
            TestContext.Current.CancellationToken);

        return body!;
    }

    /// <summary>
    /// One invite written straight to its row, with the real token minted beside it so the stored
    /// hash is the one the route will look that token up by. Every state but Open is reached by
    /// setting the column, since nothing on this branch writes any of them.
    /// </summary>
    private static string Issued(
        KairosApp app, string prefill, Action<PersonalInvite>? adjust = null)
    {
        var (token, hash) = InviteWriter.NewToken();

        app.Seed(context =>
        {
            var invite = new PersonalInvite
            {
                Id = Guid.CreateVersion7(),
                LinkId = LinkId,
                TokenHash = hash,
                Prefill = prefill,
                CreatedAt = LongAgoSeconds,
            };

            adjust?.Invoke(invite);
            context.PersonalInvites.Add(invite);
        });

        return token;
    }

    /// <summary>
    /// One link on one pattern. No durations: nothing here books, and an invite is a token on the
    /// link rather than on anything the link offers.
    /// </summary>
    private static KairosApp Seeded()
    {
        var app = new KairosApp();

        app.Seed(context =>
        {
            context.Patterns.Add(new Pattern { Id = PatternId, Name = PatternName });
            context.Links.Add(new Link
            {
                Id = LinkId,
                PatternId = PatternId,
                Slug = Slug,
                Name = LinkName,
            });
        });

        return app;
    }
}
