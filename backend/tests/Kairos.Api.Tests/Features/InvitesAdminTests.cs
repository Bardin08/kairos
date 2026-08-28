using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;

using Kairos.Api.Features.Admin;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Features.Invites;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// The operator's four invite routes, and the two properties the whole feature rests on: the URL
/// exists in exactly one response and never again, and an invite is reachable only under the link
/// it was issued on.
/// </summary>
public class InvitesAdminTests
{
    private const string Slug = "consulting";
    private const string OtherSlug = "mentorship";
    private const string LinkName = "Consulting call";
    private const string OtherLinkName = "Mentorship";
    private const string PatternName = "Weekdays";

    private const string NameField = "name";
    private const string EmailField = "email";
    private const string NotesField = "notes";
    private const string UnknownField = "favourite_colour";
    private const string SomeName = "Ada";
    private const string SomeEmail = "ada@example.com";
    private const string SomeColour = "green";

    /// <summary>What an over-long prefill value is padded with. Only its count matters.</summary>
    private const char Filler = 'x';

    /// <summary>
    /// A readable stand-in for the SHA-256 the column really holds. Nothing about these rows is
    /// resolved by token, and a readable value keeps a failure about the ordering — the choice
    /// <see cref="InviteWriterTests"/> made for the same reason.
    /// </summary>
    private const string SeededTokenHashPrefix = "seeded-token-hash-";

    /// <summary>The key present and holding null, which a typed request instance cannot express.</summary>
    private const string NullPrefillBody = """{"prefill":null}""";

    /// <summary>A body that parses and asks for nothing, for a request that must never be read.</summary>
    private const string EmptyJsonBody = "{}";

    /// <summary>The lower-cased <see cref="InviteState"/> names, which is what a row carries.</summary>
    private const string OpenState = "open";
    private const string RevokedState = "revoked";

    /// <summary>
    /// The booking route the frontend router publishes — <c>/book/:slug</c> — and the key it reads
    /// the token off. Written out here the way <see cref="Integrations.BookingMailerTests"/> writes
    /// <c>/manage/</c> rather than reaching for <c>BookingMailer.ManagePath</c>: the production
    /// constants are private, and a test that imported them would pin the URL against itself rather
    /// than against the page that has to answer it.
    /// </summary>
    private const string BookingPath = "book";

    private const string TokenQueryKey = "i";

    private static readonly Guid PatternId = Guid.CreateVersion7();
    private static readonly Guid LinkId = Guid.CreateVersion7();
    private static readonly Guid OtherLinkId = Guid.CreateVersion7();

    /// <summary>Never issued, so every route taking one has to refuse it.</summary>
    private static readonly Guid MissingInviteId = Guid.CreateVersion7();

    /// <summary>Never seeded, so the link lookup has to refuse it.</summary>
    private static readonly Guid MissingLinkId = Guid.CreateVersion7();

    /// <summary>Enough to cross a millisecond boundary, so two v7 ids order on their timestamps
    /// rather than on the random bits under them. Thread.Sleep never returns early — the reasoning
    /// <see cref="Persistence.KeyGenerationTests"/> states for the same sleep.</summary>
    private const int MillisecondsBetweenIds = 2;

    /// <summary>Any gap putting two rows in different seconds. An hour, so neither reads as adjacent.</summary>
    private const long SecondsApart = 3600;

    /// <summary>2020-01-01T00:00:00Z — comfortably before any clock this suite runs under.</summary>
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly long LongAgoSeconds = LongAgo.ToUnixTimeSeconds();

    private static readonly string InvitesPath =
        $"{ApiVersions.V1}/admin/links/{LinkId}/invites";

    /// <summary>
    /// Every route <see cref="InvitesAdminEndpoints.MapInvitesAdmin"/> maps, as a verb and a path.
    /// The two that take an invite name one that was never issued: the gate has to answer before
    /// the row is read, so a 404 from either would be a route querying the database on behalf of
    /// an anonymous caller.
    /// </summary>
    public static TheoryData<string, string> OperatorRoutes => new()
    {
        { HttpMethod.Get.Method, InvitesPath },
        { HttpMethod.Post.Method, InvitesPath },
        { HttpMethod.Post.Method, $"{InvitesPath}/{MissingInviteId}/revoke" },
        { HttpMethod.Post.Method, $"{InvitesPath}/{MissingInviteId}/regenerate" },
    };

    /// <summary>
    /// The 201 is the only place a URL ever exists, so the list has to be checked for the token
    /// itself rather than for a field name: a row that grew one under any spelling would leak it.
    /// </summary>
    [Fact]
    public async Task Create_ReturnsTheUrlExactlyOnce_AndTheListNeverRepeatsIt()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var created = await IssueAsync(client, new CreateInviteRequest
        {
            Prefill = new Dictionary<string, string>
            {
                [NameField] = SomeName,
                [EmailField] = SomeEmail,
            },
        });

        var url = new Uri(created.Url);
        url.AbsolutePath.ShouldBe($"/{BookingPath}/{Slug}");

        // The plaintext leaves the process here and nowhere else, so this is the only chance to
        // prove the URL carries the token the row was written against rather than some other string.
        var token = QueryHelpers.ParseQuery(url.Query)[TokenQueryKey].ToString();
        app.Read(context => context.PersonalInvites.AsNoTracking().Single().TokenHash)
            .ShouldBe(BookingWriter.Hash(token));

        var list = await client.GetAsync(InvitesPath, TestContext.Current.CancellationToken);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(token);

        var rows = await ReadRowsAsync(list);
        var row = rows.ShouldHaveSingleItem();
        row.Id.ShouldBe(created.Id);
        row.State.ShouldBe(OpenState);
        row.Prefill[NameField].ShouldBe(SomeName);
    }

    [Fact]
    public async Task Create_APrefillKeyTheBookingPayloadDoesNotAccept_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            InvitesPath,
            new CreateInviteRequest
            {
                Prefill = new Dictionary<string, string> { [UnknownField] = SomeColour },
            },
            TestContext.Current.CancellationToken);

        _ = await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, InvitesAdminEndpoints.PrefillInvalidCode);
        app.Read(context => context.PersonalInvites.AsNoTracking().Any()).ShouldBeFalse();
    }

    /// <summary>
    /// System.Text.Json ignores the nullable-reference annotation on the bag's value type, so a
    /// null binds, stores as <c>{"name":null}</c> and is read back typed non-null. Unrefused it
    /// reaches the invitee's form as a null in a controlled input, and their own booking POST then
    /// fails <c>[Required]</c> with a 400 they have no way to fix.
    /// </summary>
    [Fact]
    public async Task Create_APrefillValueSentAsNull_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            InvitesPath,
            new CreateInviteRequest
            {
                Prefill = new Dictionary<string, string> { [NameField] = null! },
            },
            TestContext.Current.CancellationToken);

        _ = await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, InvitesAdminEndpoints.PrefillInvalidCode);
        app.Read(context => context.PersonalInvites.AsNoTracking().Any()).ShouldBeFalse();
    }

    /// <summary>
    /// The bag is optional, and a caller that sends the key holding null means what a caller that
    /// omits it means. Bound onto a property with an initializer the null wins, the validator walks
    /// a null bag, and the operator is handed an unhandled NullReferenceException as a 500 — a
    /// refusal they cannot read for a request that is not even wrong.
    /// </summary>
    [Fact]
    public async Task Create_APrefillSentAsNull_IsTheSameAsSendingNoneAtAll()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        // Written as JSON rather than through the record: the defect is in what the binder does
        // with an explicit null, which a typed instance cannot express.
        var response = await client.PostAsync(
            InvitesPath,
            new StringContent(NullPrefillBody, Encoding.UTF8, MediaTypeNames.Application.Json),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var rows = await ReadRowsAsync(
            await client.GetAsync(InvitesPath, TestContext.Current.CancellationToken));

        rows.ShouldHaveSingleItem().Prefill.ShouldBeEmpty();
    }

    /// <summary>
    /// The message half of the contract. One code answers every prefill problem, so the sentence
    /// is the only thing telling the operator WHICH key to fix and what was wrong with it — and
    /// this is the bag whose fault is a length, which no other test posts over HTTP.
    /// </summary>
    [Fact]
    public async Task Create_APrefillValueLongerThanTheBookingPayloadAccepts_IsRefusedNamingTheKey()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            InvitesPath,
            new CreateInviteRequest
            {
                Prefill = new Dictionary<string, string>
                {
                    [NotesField] = new(Filler, CreateBookingRequest.MaxNotesLength + 1),
                },
            },
            TestContext.Current.CancellationToken);

        var problem = await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, InvitesAdminEndpoints.PrefillInvalidCode);

        problem.Title.ShouldContain(NotesField);
        app.Read(context => context.PersonalInvites.AsNoTracking().Any()).ShouldBeFalse();
    }

    /// <summary>An invite born expired is a bug, and issuance is the only place that can tell.</summary>
    [Fact]
    public async Task Create_ADeadlineAlreadyPast_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            InvitesPath,
            new CreateInviteRequest { ExpiresAt = LongAgo },
            TestContext.Current.CancellationToken);

        _ = await ShouldBeRefusedAsync(
            response, HttpStatusCode.BadRequest, InvitesAdminEndpoints.DeadlinePastCode);
        app.Read(context => context.PersonalInvites.AsNoTracking().Any()).ShouldBeFalse();
    }

    [Fact]
    public async Task Create_ForALinkThatDoesNotExist_IsNotFound()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/admin/links/{MissingLinkId}/invites",
            new CreateInviteRequest(),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The answer to a lost URL: the old one stops working, the replacement carries what the
    /// operator already filled in, and the two are different tokens.
    /// </summary>
    [Fact]
    public async Task Regenerate_RevokesTheOldOneAndKeepsThePrefill()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var original = await IssueAsync(client, new CreateInviteRequest
        {
            Prefill = new Dictionary<string, string> { [NameField] = SomeName },
        });

        var response = await client.PostAsync(
            $"{InvitesPath}/{original.Id}/regenerate", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replacement = await response.Content.ReadFromJsonAsync<InviteCreatedResponse>(
            TestContext.Current.CancellationToken);

        replacement!.Id.ShouldNotBe(original.Id);
        replacement.Url.ShouldNotBe(original.Url);

        // The order the list route promises, over the pair that always ties on CreatedAt: the
        // replacement on top, the row it replaced directly under it.
        var rows = await ReadRowsAsync(
            await client.GetAsync(InvitesPath, TestContext.Current.CancellationToken));

        rows.Select(row => row.Id).ShouldBe([replacement.Id, original.Id]);
        rows[0].State.ShouldBe(OpenState);
        rows[0].Prefill[NameField].ShouldBe(SomeName);
        rows[1].State.ShouldBe(RevokedState);
    }

    /// <summary>
    /// Regenerate answers "the URL was lost". Nothing was lost when somebody already booked
    /// through it, and minting a replacement would hand a second single-use token to a person who
    /// has already had their one booking.
    /// </summary>
    [Fact]
    public async Task Regenerate_AnInviteSomebodyAlreadyBookedThrough_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var original = await IssueAsync(client, new CreateInviteRequest());
        Rewrite(app, invite => invite.ConsumedAt = LongAgoSeconds);

        await ShouldNotRegenerateAsync(app, client, original.Id);
    }

    /// <summary>
    /// The deadline is copied verbatim, so a lapsed invite would reissue as one born expired —
    /// the exact state <see cref="InvitesAdminEndpoints.DeadlinePastCode"/> refuses at creation.
    /// </summary>
    [Fact]
    public async Task Regenerate_AnInviteWhoseDeadlineHasPassed_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var original = await IssueAsync(client, new CreateInviteRequest());
        Rewrite(app, invite => invite.ExpiresAt = LongAgoSeconds);

        await ShouldNotRegenerateAsync(app, client, original.Id);
    }

    /// <summary>
    /// The ruling: only an open invite may be regenerated. Reissuing one the operator deliberately
    /// took back is what pressing Create does, with no ambiguity about whether the old URL is meant
    /// to work again — and one rule is a rule the operator can hold in their head.
    /// </summary>
    [Fact]
    public async Task Regenerate_AnInviteTheOperatorAlreadyTookBack_IsRefused()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var original = await IssueAsync(client, new CreateInviteRequest());

        var revoked = await client.PostAsync(
            $"{InvitesPath}/{original.Id}/revoke", null, TestContext.Current.CancellationToken);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldNotRegenerateAsync(app, client, original.Id);
    }

    /// <summary>The second press is the operator pressing twice, not an error worth a page.</summary>
    [Fact]
    public async Task Revoke_TwiceInARow_IsNotAnError()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var created = await IssueAsync(client, new CreateInviteRequest());
        var path = $"{InvitesPath}/{created.Id}/revoke";

        var first = await client.PostAsync(path, null, TestContext.Current.CancellationToken);
        var second = await client.PostAsync(path, null, TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// An invite belongs to the link it was issued on. Without the scoping every id-taking route
    /// here answers for any invite in the database, and the link segment becomes decoration.
    /// </summary>
    [Fact]
    public async Task Revoke_ThroughAnotherLinksId_IsNotFound()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var created = await IssueAsync(client, new CreateInviteRequest());

        var response = await client.PostAsync(
            $"{ApiVersions.V1}/admin/links/{OtherLinkId}/invites/{created.Id}/revoke",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Read(context => context.PersonalInvites.AsNoTracking().Single().RevokedAt)
            .ShouldBeNull();
    }

    /// <summary>
    /// The same scoping as <see cref="Revoke_ThroughAnotherLinksId_IsNotFound"/>, and the more
    /// dangerous of the two: this route mints a spendable URL, so an invite reachable under a link
    /// it was never issued on is a booking URL for the wrong link.
    /// </summary>
    [Fact]
    public async Task Regenerate_ThroughAnotherLinksId_IsNotFound()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var created = await IssueAsync(client, new CreateInviteRequest());

        var response = await client.PostAsync(
            $"{ApiVersions.V1}/admin/links/{OtherLinkId}/invites/{created.Id}/regenerate",
            null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Nothing minted and nothing taken back: a replacement written under the wrong link would
        // be a second spendable token, and the original would have been revoked to pay for it.
        app.Read(context => context.PersonalInvites.AsNoTracking().Count()).ShouldBe(1);
        app.Read(context => context.PersonalInvites.AsNoTracking().Single().RevokedAt)
            .ShouldBeNull();
    }

    [Fact]
    public async Task Revoke_AnInviteThatDoesNotExist_IsNotFound()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.PostAsync(
            $"{InvitesPath}/{MissingInviteId}/revoke", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Newest first on the stamp, which is the clause a pair sharing a second never reaches. Both
    /// halves of the ordering need their own case: with the rows tied, ascending and descending
    /// return the same list and the sort under test says nothing.
    /// </summary>
    [Fact]
    public async Task List_TwoInvitesFromDifferentSeconds_IsNewestFirst()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var older = Guid.CreateVersion7();
        Thread.Sleep(MillisecondsBetweenIds);
        var newer = Guid.CreateVersion7();

        SeedInvite(app, older, LongAgoSeconds);
        SeedInvite(app, newer, LongAgoSeconds + SecondsApart);

        var rows = await ReadRowsAsync(
            await client.GetAsync(InvitesPath, TestContext.Current.CancellationToken));

        rows.Select(row => row.Id).ShouldBe([newer, older]);
    }

    /// <summary>
    /// Newest first, and on the id rather than on however the rows happen to be stored. CreatedAt
    /// is stamped in unix SECONDS, so a regenerate — one row revoked and its replacement added —
    /// reliably produces a pair the CreatedAt ordering cannot separate, and what comes back for a
    /// tie is otherwise whatever the scan walked into.
    /// </summary>
    /// <remarks>
    /// Seeded rather than issued through the route, and stored newest-FIRST on purpose. Issued
    /// through the route the ids ascend in step with the storage order, so the guaranteed answer
    /// and the incidental one agree and neither is being tested — measured: with the tiebreak
    /// deleted, a test written that way still passed.
    ///
    /// Stored this way round the two were meant to disagree, and they no longer do. Once
    /// <see cref="InviteReader"/> began joining Links for the link's name, the plan the tie falls
    /// back on changed and the incidental order started giving the right answer here too — so this
    /// case survives the tiebreak being DELETED, and only still fails when it is inverted. The
    /// guarantee is pinned by
    /// <see cref="InviteReaderTests.AllAsync_TwoInvitesTiedOnTheSecond_OrdersOnTheIdRatherThanOnStorageOrder"/>,
    /// which is where it stays when Task 7 of the invites-placement plan retires this route.
    ///
    /// The ids are UUIDv7 and reach SQLite as canonical hyphenated TEXT, which sorts
    /// lexicographically and therefore chronologically. That is what
    /// <see cref="Persistence.KeyGenerationTests"/> pins end to end — the sleep included, since v7
    /// orders on a millisecond and fills the rest with random bits rather than a counter.
    /// </remarks>
    [Fact]
    public async Task List_TwoInvitesStampedInTheSameSecond_OrdersOnTheIdRatherThanOnStorageOrder()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var older = Guid.CreateVersion7();
        Thread.Sleep(MillisecondsBetweenIds);
        var newer = Guid.CreateVersion7();

        SeedInvite(app, newer, LongAgoSeconds);
        SeedInvite(app, older, LongAgoSeconds);

        var rows = await ReadRowsAsync(
            await client.GetAsync(InvitesPath, TestContext.Current.CancellationToken));

        rows.Select(row => row.Id).ShouldBe([newer, older]);
    }

    [Fact]
    public async Task List_ForALinkThatDoesNotExist_IsNotFound()
    {
        using var app = Seeded();
        using var client = app.CreateOperatorClient();

        var response = await client.GetAsync(
            $"{ApiVersions.V1}/admin/links/{MissingLinkId}/invites",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 401 and not 302. These routes are reached with fetch, and a fetch cannot follow a redirect
    /// to the identity provider's origin — it fails as an opaque CORS error saying nothing about
    /// the session having expired. It is also the assertion that dies if any of these routes is
    /// ever mapped onto the anonymous group instead of <see cref="AdminGroup.MapAdmin"/>.
    /// </summary>
    /// <remarks>
    /// One row per route rather than one case for the list and an inference for the other three:
    /// <see cref="EndpointPolicySweepTests"/> proves every route under the admin prefix carries the
    /// policy, which is the stronger statement about routes nobody has written a case for, but it
    /// says nothing about what a caller holding no session is actually answered.
    /// </remarks>
    [Theory]
    [MemberData(nameof(OperatorRoutes))]
    public async Task EveryOperatorRoute_WithoutASession_IsUnauthorizedRatherThanARedirect(
        string method,
        string path)
    {
        using var app = Seeded();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (request.Method != HttpMethod.Get)
        {
            // A body, because the request has to be one routing will match at all. A POST carrying
            // no content misses the route that declares it consumes JSON and is answered by the
            // API's catch-all 404 instead — measured, and it says nothing about the gate. The gate
            // still answers before anything reads this, which is why the content is empty.
            request.Content = new StringContent(
                EmptyJsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task ShouldNotRegenerateAsync(KairosApp app, HttpClient client, Guid inviteId)
    {
        var response = await client.PostAsync(
            $"{InvitesPath}/{inviteId}/regenerate", null, TestContext.Current.CancellationToken);

        _ = await ShouldBeRefusedAsync(
            response, HttpStatusCode.Conflict, InvitesAdminEndpoints.InviteNotOpenCode);

        // The refusal has to be the whole answer: a replacement written and then refused is a
        // spendable token nobody was told about.
        app.Read(context => context.PersonalInvites.AsNoTracking().Count()).ShouldBe(1);
    }

    private static async Task<ProblemWithCode> ShouldBeRefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemWithCode>(
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(status);
        problem!.Code.ShouldBe(code);

        return problem;
    }

    private static async Task<InviteCreatedResponse> IssueAsync(
        HttpClient client, CreateInviteRequest request)
    {
        var response = await client.PostAsJsonAsync(
            InvitesPath, request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<InviteCreatedResponse>(
            TestContext.Current.CancellationToken);

        return created!;
    }

    private static async Task<InviteView[]> ReadRowsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await response.Content.ReadFromJsonAsync<InviteView[]>(
            TestContext.Current.CancellationToken);

        return rows!;
    }

    /// <summary>
    /// One invite written straight to its row, so the test chooses both its id and the order it is
    /// stored in — neither of which is the route's to give away. Every field but the id is the
    /// same, the tie being the whole point; the hash is derived from the id because that column is
    /// uniquely indexed.
    /// </summary>
    private static void SeedInvite(KairosApp app, Guid id, long createdAt) => app.Seed(context =>
        context.PersonalInvites.Add(new PersonalInvite
        {
            Id = id,
            LinkId = LinkId,
            TokenHash = $"{SeededTokenHashPrefix}{id}",
            Prefill = InvitePrefill.Empty,
            CreatedAt = createdAt,
        }));

    /// <summary>
    /// Ages or spends the single seeded invite in place. The consuming UPDATE lives inside
    /// BookingWriter's transaction and does not exist yet, and creation refuses a deadline that
    /// has already passed — so both states are reached by writing the row the seeder writes it.
    /// </summary>
    private static void Rewrite(KairosApp app, Action<PersonalInvite> change) =>
        app.Seed(context => change(context.PersonalInvites.Single()));

    /// <summary>
    /// Two links on one pattern, so an invite issued on the first can be asked for under the
    /// second. No durations: nothing here books, and an invite is a token on the link rather than
    /// on anything the link offers.
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
            context.Links.Add(new Link
            {
                Id = OtherLinkId,
                PatternId = PatternId,
                Slug = OtherSlug,
                Name = OtherLinkName,
            });
        });

        return app;
    }

    /// <summary>
    /// The two halves of a refusal a caller acts on: the code it branches on, and the sentence it
    /// shows. <see cref="DomainExceptionHandler"/> writes the domain message as the title.
    /// </summary>
    private sealed record ProblemWithCode(string Code, string Title);
}
