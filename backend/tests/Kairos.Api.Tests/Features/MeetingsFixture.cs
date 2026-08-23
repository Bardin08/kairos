using System.Net.Http.Json;

using Kairos.Api.Features.Bookings;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using NodaTime;

namespace Kairos.Api.Tests.Features;

/// <summary>
/// One seeded world for every suite about meetings: two links on the same pattern, so a booking
/// made through one can be asserted against the other's offer. Shared rather than copied — two
/// descriptions of the same world drift, and the cross-link margin rule is exactly the thing that
/// stops being tested when they do.
/// </summary>
/// <remarks>
/// <see cref="MeetingsAdminTests"/> keeps its own seed and is deliberately not folded into this
/// one. Its world differs in four ways, and every one of them is load-bearing for what that suite
/// asserts: meetings are 25 minutes rather than 30, the booker reads them in <c>Asia/Tokyo</c>
/// rather than <c>Europe/Warsaw</c> — the point being a zone that is not the organizer's UTC —
/// the pattern opens Monday alone rather than all seven days, and its bookings sit on three fixed
/// consecutive days its range filter tests assert on both edges of. Merging the two would not
/// remove a duplicate; it would change what those tests mean, which is worse than two fixtures.
/// This world is instead the one <see cref="ProposalHoldTests"/> seeded, widened by a second link
/// and lifted out of it — that suite now calls this one, and no assertion in it moved to make that
/// true. The second link is inert there: availability is one calendar, and nothing in it books or
/// proposes through the second slug.
/// </remarks>
public static class MeetingsFixture
{
    public const string Slug = "consulting";
    public const string OtherSlug = "mentorship";
    public const int DurationMinutes = 30;
    public const string Day = "2030-09-09";
    public const string BookedInstant = $"{Day}T11:00:00Z";

    private const string PatternName = "Weekdays";
    private const string LinkName = "Consulting call";
    private const string OtherLinkName = "Mentorship";
    private const int NineAm = 9 * 60;
    private const int FivePm = 17 * 60;
    private const string BookerName = "Ada Lovelace";
    private const string BookerEmail = "ada@example.com";
    private const string BookerZone = "Europe/Warsaw";

    /// <summary>Seeds the world and books <see cref="BookedInstant"/>. Returns the booking's id.</summary>
    public static async Task<Guid> BookAsync(KairosApp app, HttpClient client)
    {
        Seed(app);

        var created = await client.PostAsJsonAsync(
            $"{ApiVersions.V1}/bookings",
            new CreateBookingRequest
            {
                Slug = Slug,
                Start = BookedInstant,
                DurationMinutes = DurationMinutes,
                Name = BookerName,
                Email = BookerEmail,
                TimeZone = BookerZone,
            },
            TestContext.Current.CancellationToken);

        created.EnsureSuccessStatusCode();
        var booking = await created.Content.ReadFromJsonAsync<BookingResponse>(
            TestContext.Current.CancellationToken);

        return booking!.Id;
    }

    public static async Task<IReadOnlyList<string>> SlotsOnOtherLinkAsync(KairosApp app)
    {
        // A bare JSON array, and the parameter is `duration`. See ProposalHoldTests for why the
        // other spelling is worse than a compile error: it passes, against a different path.
        var answer = await app.Client.GetFromJsonAsync<string[]>(
            $"{ApiVersions.V1}/links/{OtherSlug}/slots?from={Day}&to={Day}&duration={DurationMinutes}",
            TestContext.Current.CancellationToken);

        return answer!;
    }

    public static void Seed(KairosApp app) => app.Seed(context =>
    {
        if (context.Links.Any(link => link.Slug == Slug))
        {
            return;
        }

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

        AddLink(context, pattern.Id, Slug, LinkName);
        AddLink(context, pattern.Id, OtherSlug, OtherLinkName);
    });

    private static void AddLink(KairosDbContext context, Guid patternId, string slug, string name)
    {
        var link = new Link
        {
            Id = Guid.CreateVersion7(),
            PatternId = patternId,
            Slug = slug,
            Name = name,
            Active = true,
        };

        context.Links.Add(link);
        context.LinkDurations.Add(new LinkDuration
        {
            // No Id: the key is the (LinkId, Minutes) pair.
            LinkId = link.Id,
            Minutes = DurationMinutes,
            SortOrder = 0,
        });
    }
}
