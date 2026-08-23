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
/// descriptions of one world drift, and the cross-link margin rule is what stops being tested.
/// </summary>
/// <remarks>
/// <see cref="MeetingsAdminTests"/> keeps its own seed deliberately: its meetings are 25 minutes
/// not 30, its booker reads them in <c>Asia/Tokyo</c> rather than the organizer's UTC, its
/// pattern opens Monday alone, and its bookings sit on three fixed consecutive days its range
/// filter asserts both edges of. This world is <see cref="ProposalHoldTests"/>' own, widened by a
/// second link and lifted out with no assertion moved; that suite uses only the first slug.
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
    /// <summary>Deliberately not the organizer's, which is UTC. Public because the proposal read is
    /// asserted to answer THIS zone rather than the organizer's.</summary>
    public const string BookerZone = "Europe/Warsaw";

    /// <summary>Seeds the world before booking <see cref="BookedInstant"/>.</summary>
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
