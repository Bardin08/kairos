using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Bookings;

public sealed record CreateBookingRequest
{
    [Required]
    public required string Slug { get; init; }

    /// <summary>One of the strings <c>GET /links/{slug}/slots</c> returned, verbatim.</summary>
    [Required]
    public required string Start { get; init; }

    /// <summary>
    /// Required here even though the slots read may omit it for a single-duration link: the
    /// writer stores an end instant, and resolving the default a second time would be a copy of
    /// the resolution <see cref="SlotQuery"/> already owns.
    /// </summary>
    [Range(MinDurationMinutes, MaxDurationMinutes)]
    public required int DurationMinutes { get; init; }

    [Required]
    [StringLength(MaxNameLength)]
    public required string Name { get; init; }

    [Required]
    [EmailAddress]
    [StringLength(MaxEmailLength)]
    public required string Email { get; init; }

    /// <summary>The booker's IANA zone — the one they read the times in, not the organizer's.</summary>
    [Required]
    [StringLength(MaxTimeZoneLength)]
    public required string TimeZone { get; init; }

    [StringLength(MaxNotesLength)]
    public string Notes { get; init; } = string.Empty;

    private const int MinDurationMinutes = 1;
    private const int MaxDurationMinutes = 1440;
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;
    private const int MaxTimeZoneLength = 64;
    private const int MaxNotesLength = 2000;
}

/// <summary>
/// Where the meeting moves to. Its length does not change — a different duration is a different
/// booking, and keeping it fixed is what lets the identifier stay the same.
/// </summary>
public sealed record RescheduleBookingRequest
{
    /// <summary>One of the strings <c>GET /links/{slug}/slots</c> returned, verbatim.</summary>
    [Required]
    public required string Start { get; init; }
}

/// <summary>
/// One booking as every one of these endpoints answers it. <see cref="ManageToken"/> is non-null
/// exactly once, in the 201 from create: after that only its hash exists, so nothing can hand it
/// back a second time.
/// </summary>
/// <param name="CalendarSync">
/// Where this booking is in the organizer's calendar, lower-cased from
/// <see cref="CalendarSyncState"/>: <c>synced</c>, <c>failed</c>, <c>pending</c> or
/// <c>cancelled</c>. Null means nothing was ever attempted — the ordinary state of a deployment
/// with no calendar connected, and not a fault. Everything a UI needs to say "this meeting is not
/// in your calendar", and nothing more: the provider's own error text stays out of a response an
/// invitee's manage token can read, because it is unfiltered third-party prose that routinely
/// carries the organizer's account address inside it.
/// </param>
/// <param name="ConferenceUrl">
/// Where to join, or null if this meeting has no second channel. The invitee already has this URL
/// in their invitation; it is here so a page can show it without one.
/// </param>
public sealed record BookingResponse(
    Guid Id,
    string Slug,
    string Start,
    string End,
    int DurationMinutes,
    string Status,
    int Sequence,
    string Name,
    string Email,
    string TimeZone,
    string Notes,
    string? ManageToken,
    string? CalendarSync,
    string? ConferenceUrl);

/// <summary>
/// What the invitee is being asked, and what they are being asked to leave. <see cref="State"/> is
/// the lower-cased <see cref="ProposalState"/>, so a page can render an answered proposal as
/// finished rather than as an error.
/// </summary>
public sealed record ProposalResponse(
    Guid BookingId,
    string LinkName,
    string CurrentStart,
    string CurrentEnd,
    string ProposedStart,
    string ProposedEnd,
    string ExpiresAt,
    string TimeZone,
    string State);

public static class BookingsEndpoints
{
    /// <summary>
    /// The requested instant is not one the link offers — outside availability, inside minimum
    /// notice, over the daily cap, or padded out by a neighbouring meeting's margin.
    /// </summary>
    public const string SlotUnavailableCode = "slot_unavailable";

    public const string InstantInvalidCode = "instant_invalid";

    public const string TimeZoneInvalidCode = "timezone_invalid";

    public const string BookingCancelledCode = "booking_cancelled";

    /// <summary>Somebody already answered this proposal, or it was taken back.</summary>
    public const string ProposalClosedCode = "proposal_closed";

    /// <summary>Nobody answered it in time. A separate sentence on the page, so a separate code.</summary>
    public const string ProposalExpiredCode = "proposal_expired";

    private const int SecondsPerMinute = 60;

    public static RouteGroupBuilder MapBookings(this RouteGroupBuilder group)
    {
        // Every route here is anonymous, three of the four write, and create mails an address the
        // caller chose from the organizer's relay — so the budget is stated at the mapping site
        // rather than left to a global default nobody reads.
        group.MapPost("/bookings", CreateAsync).WithName("CreateBooking")
            .RateLimited(RateLimitClass.AnonymousWrite);
        group.MapGet("/bookings/{token}", GetAsync).WithName("GetBooking")
            .RateLimited(RateLimitClass.AnonymousRead);
        group.MapPost("/bookings/{token}/cancel", CancelAsync).WithName("CancelBooking")
            .RateLimited(RateLimitClass.AnonymousWrite);
        group.MapPost("/bookings/{token}/reschedule", RescheduleAsync).WithName("RescheduleBooking")
            .RateLimited(RateLimitClass.AnonymousWrite);

        // The invitee's side of an operator's proposal. Anonymous like everything else keyed by a
        // token, and budgeted the same way: a lookup by hash that computes nothing, against writes
        // one of which moves a meeting and mails somebody about it.
        group.MapGet("/bookings/proposals/{token}", GetProposalAsync).WithName("GetProposal")
            .RateLimited(RateLimitClass.AnonymousRead);
        group.MapPost("/bookings/proposals/{token}/accept", AcceptProposalAsync).WithName("AcceptProposal")
            .RateLimited(RateLimitClass.AnonymousWrite);
        group.MapPost("/bookings/proposals/{token}/decline", DeclineProposalAsync).WithName("DeclineProposal")
            .RateLimited(RateLimitClass.AnonymousWrite);
        return group;
    }

    private static async Task<Created<BookingResponse>> CreateAsync(
        CreateBookingRequest request,
        KairosDbContext context,
        BookingMover mover,
        BookingWriter writer,
        CalendarSyncService calendars,
        BookingMailer mailer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // Read once, used for the availability re-check below. The writer's own clock only stamps
        // CreatedAt; minimum notice is measured from this instant alone, so the page's offer and
        // this check cannot disagree about where the notice boundary sits.
        var now = clock.GetCurrentInstant();
        var start = BookingMover.ParseInstant(request.Start);
        var bookerZone = ResolveZone(request.TimeZone);

        var offered = await mover.RequireOfferedAsync(
            request.Slug, request.DurationMinutes, start, now, cancellationToken);

        // The link and the margin come off the re-check itself, which is the seam this endpoint
        // exists to keep tight: the read and the write must agree about the resolved margin, or
        // the page offers a slot the writer refuses. Resolving it a second time here — another
        // Links read and another `?? settings.MarginMinutes` — would be a copy free to drift.
        var result = writer.TryCreate(
            offered.LinkId,
            request.Slug,
            start,
            Duration.FromMinutes(request.DurationMinutes),
            new BookerDetails(request.Name, request.Email, bookerZone),
            request.Notes,
            offered.Limits.MarginMinutes);

        if (!result.Success)
        {
            throw DomainException.Conflict(
                BookingWriter.SlotTakenReason, "That time was taken while this booking was being made.");
        }

        var booking = await LoadAsync(context, result.Id, cancellationToken);

        // After the booking is committed and before the 201, in the request rather than behind a
        // queue. Inline costs the booker whatever the provider's round trip costs; a background
        // job would answer 201 for a meeting whose calendar state nothing has established yet,
        // and this endpoint is also the only place holding the loaded booking. It cannot fail the
        // request — SyncAsync has no throwing path — so the worst inline buys is a slower 201.
        await calendars.SyncAsync(booking, cancellationToken);

        // After the sync, so the confirmation describes a meeting the organizer's calendar already
        // knows about, and never before the write committed. The plaintext manage token exists
        // only here, in memory, for the length of this call — it is what the confirmation is for,
        // and it is why the send happens on this thread instead of from a queue that would have to
        // store it. The mailer swallows its own failures, so nothing here can turn a relay being
        // down into a booking the organizer never gets.
        await mailer.SendConfirmationAsync(booking, result.ManageToken!, cancellationToken);

        return TypedResults.Created(
            $"{ApiVersions.V1}/bookings/{result.ManageToken}",
            await ToResponseAsync(context, booking, result.ManageToken, cancellationToken));
    }

    private static async Task<BookingResponse> GetAsync(
        string token,
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        await ToResponseAsync(
            context, await FindAsync(context, token, cancellationToken), null, cancellationToken);

    /// <summary>
    /// The invitee calls their own meeting off, with the token they arrived holding.
    /// </summary>
    /// <remarks>
    /// Everything that happens to the booking is <see cref="BookingCanceller"/>'s, and this is
    /// half of why it exists: the operator's cancel in <see cref="Admin.MeetingsAdminEndpoints"/>
    /// is the same cancellation asked for by somebody else, and two copies of the status write,
    /// the sequence, the announcement, the contention rule and the rule-14 tail had already begun
    /// to differ from one another.
    ///
    /// No reason: the invitee knows why, and the null column is what records that this was their
    /// cancellation rather than the operator's.
    /// </remarks>
    private static async Task<BookingResponse> CancelAsync(
        string token,
        KairosDbContext context,
        BookingCanceller canceller,
        CancellationToken cancellationToken)
    {
        var booking = await FindAsync(context, token, cancellationToken);

        // `token` is the plaintext manage token off the route — the invitee opened this URL, so it
        // is in hand for the length of the request and is never written down.
        await canceller.CancelAsync(booking, reason: null, token, cancellationToken);

        return await ToResponseAsync(context, booking, null, cancellationToken);
    }

    private static async Task<BookingResponse> RescheduleAsync(
        string token,
        RescheduleBookingRequest request,
        KairosDbContext context,
        BookingMover mover,
        CalendarSyncService calendars,
        BookingMailer mailer,
        ProposalWriter proposals,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var start = BookingMover.ParseInstant(request.Start);

        var booking = await FindAsync(context, token, cancellationToken);
        BookingMover.RequireMovable(booking);

        await mover.ReleaseAndCheckAsync(booking, start, now, compensate: null, cancellationToken);

        // An operator's suggestion about where this meeting should go stops applying the moment
        // the invitee moves it themselves, and it is closed in the save that moves it — never on
        // the failing paths inside the release, where nothing happened and the question stands.
        await proposals.WithdrawOpenAsync(booking.Id, now, cancellationToken);
        await mover.MoveToAsync(booking, start);

        // Here, at the end, and never in the release phase above. That release is bookkeeping —
        // the booking is set cancelled and put back confirmed on every path out of this method,
        // the failing ones included — so a calendar hook hung on the status change would withdraw
        // the event for every meeting that merely moved, and an attendee who had already accepted
        // would watch it vanish. Exactly the trap the booking.cancelled announcement is kept out
        // of, and pinned by the same pair of tests.
        await calendars.RescheduleAsync(booking, cancellationToken);

        // Then the invitee, so the mail describes a calendar the organizer's side already agrees
        // with. Same token, same reasoning as the cancellation: it arrived in the URL, it stays in
        // memory for this request, and nothing persists it. Sequence was advanced above and the
        // UID is the booking id, which together are what make this land as a change rather than as
        // a second meeting.
        await mailer.SendRescheduleAsync(booking, token, cancellationToken);

        return await ToResponseAsync(context, booking, null, cancellationToken);
    }

    private static async Task<ProposalResponse> GetProposalAsync(
        string token,
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        Describe(
            await FindProposalAsync(context, token, cancellationToken),
            await TimeZoneIdAsync(context, cancellationToken));

    /// <summary>
    /// The invitee agrees, and only now does anything become true.
    /// </summary>
    /// <remarks>
    /// Release, re-check, re-place — <see cref="RescheduleAsync"/>'s shape, and for the same
    /// reason: a confirmed booking blocks its own calendar, so it is released before the re-check
    /// or a move an hour later is refused by the meeting being moved.
    ///
    /// The proposal is closed in that same save, and there is no exclusion parameter anywhere: a
    /// row that is no longer Open is a row neither conflict reader can see. That is not what
    /// carries the re-check, though, and it is worth being exact about which line does. Both
    /// readers also require the booking under a hold to be Confirmed, so the release alone already
    /// makes this proposal's own hold invisible for the length of the re-check. What closing it
    /// here buys is everything after: the re-place puts the booking back Confirmed, and a hold
    /// still Open at that moment would block the slot the meeting now occupies, refuse every later
    /// proposal through the filtered unique index, and reach the sweeper as a suggestion nobody
    /// answered. Same save, so no window exists in which one is true without the other.
    ///
    /// Every path out puts the booking back confirmed, the failing ones included. A proposal
    /// closed by a re-check that failed goes to Withdrawn rather than back to Open: the slot it
    /// was holding is gone, so re-offering it would be offering something that is not there.
    /// </remarks>
    private static async Task<BookingResponse> AcceptProposalAsync(
        string token,
        KairosDbContext context,
        BookingMover mover,
        CalendarSyncService calendars,
        BookingMailer mailer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var proposal = await FindProposalAsync(context, token, cancellationToken);
        RequireOpen(proposal, now);

        var booking = await Tracked(context)
            .SingleAsync(row => row.Id == proposal.BookingId, cancellationToken);

        BookingMover.RequireMovable(booking);

        var start = Instant.FromUnixTimeSeconds(proposal.StartUtc);
        proposal.State = ProposalState.Accepted;
        proposal.DecidedAt = now.ToUnixTimeSeconds();

        await mover.ReleaseAndCheckAsync(
            booking,
            start,
            now,
            compensate: () => proposal.State = ProposalState.Withdrawn,
            cancellationToken);

        await mover.MoveToAsync(booking, start);

        // Rule 14's tail, in the order the reschedule above keeps it and never in the release
        // phase: that release is bookkeeping the failing paths undo, and a calendar hook hung on
        // it would withdraw the event for a meeting that merely moved.
        await calendars.RescheduleAsync(booking, cancellationToken);

        // No manage token, and none can be had: the URL the invitee opened carried the PROPOSAL's
        // token and only the manage token's SHA-256 is stored. The mail goes without that link.
        await mailer.SendRescheduleAsync(booking, manageToken: null, cancellationToken);

        return await ToResponseAsync(context, booking, null, cancellationToken);
    }

    /// <summary>
    /// The invitee says no. The booking is not opened for writing at all, which is what makes
    /// "a declined proposal leaves the meeting exactly as it was" true by construction.
    /// </summary>
    private static async Task<ProposalResponse> DeclineProposalAsync(
        string token,
        KairosDbContext context,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var proposal = await FindProposalAsync(context, token, cancellationToken);
        RequireOpen(proposal, now);

        proposal.State = ProposalState.Declined;
        proposal.DecidedAt = now.ToUnixTimeSeconds();
        await context.SaveChangesAsync(cancellationToken);

        return Describe(proposal, await TimeZoneIdAsync(context, cancellationToken));
    }

    /// <summary>
    /// Open and unlapsed, or nothing happens. The two refusals are separate codes because they
    /// are separate sentences on the page: somebody already answered this, against nobody
    /// answered it in time.
    /// </summary>
    private static void RequireOpen(BookingProposal proposal, Instant now)
    {
        if (proposal.State != ProposalState.Open)
        {
            throw DomainException.Conflict(ProposalClosedCode, "This suggestion has already been answered.");
        }

        if (proposal.ExpiresAt <= now.ToUnixTimeSeconds())
        {
            throw DomainException.Conflict(ProposalExpiredCode, "This suggestion has lapsed.");
        }
    }

    /// <summary>
    /// By hash against the unique index, and a token that matches nothing is a 404 whatever its
    /// shape — the same answer a real token for a deleted proposal gets.
    /// </summary>
    private static async Task<BookingProposal> FindProposalAsync(
        KairosDbContext context,
        string token,
        CancellationToken cancellationToken)
    {
        var hash = BookingWriter.Hash(token);
        return await context.BookingProposals
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Link)
            .FirstOrDefaultAsync(proposal => proposal.TokenHash == hash, cancellationToken)
            ?? throw DomainException.NotFound("Proposal");
    }

    /// <summary>
    /// The proposal as the invitee's page draws it. The organizer's zone rides along because both
    /// times on that page are the organizer's calendar, and a column of times belonging to nobody
    /// in particular is how a call gets missed.
    /// </summary>
    private static ProposalResponse Describe(BookingProposal proposal, string timeZoneId) =>
        new(
            proposal.BookingId,
            proposal.Booking.Link.Name,
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.Booking.StartUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.Booking.EndUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.StartUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.EndUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.ExpiresAt)),
            timeZoneId,
            proposal.State.ToString().ToLowerInvariant());

    private static async Task<string> TimeZoneIdAsync(
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        await context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// The manage token is never stored, so the lookup is by its hash against the unique index.
    /// A token that matches nothing is a 404 whatever its shape — the same answer a real token
    /// for a deleted booking would get, and one that tells a guesser nothing.
    /// </summary>
    private static async Task<Booking> FindAsync(
        KairosDbContext context,
        string token,
        CancellationToken cancellationToken)
    {
        var hash = BookingWriter.Hash(token);
        return await Tracked(context)
            .FirstOrDefaultAsync(booking => booking.ManageTokenHash == hash, cancellationToken)
            ?? throw DomainException.NotFound("Booking");
    }

    private static Task<Booking> LoadAsync(
        KairosDbContext context,
        Guid id,
        CancellationToken cancellationToken) =>
        Tracked(context).SingleAsync(booking => booking.Id == id, cancellationToken);

    private static IQueryable<Booking> Tracked(KairosDbContext context) =>
        context.Bookings
            .Include(booking => booking.Link)
            .Include(booking => booking.Participants);

    /// <summary>
    /// The booking, plus the two facts about it that live outside its own row.
    /// </summary>
    /// <remarks>
    /// Two reads rather than a join. Both tables are keyed by booking id, both are empty on a
    /// deployment with nothing connected, and both are read once per response on an endpoint
    /// nobody hits in a loop — so the cost is two indexed lookups against SQLite, and what it buys
    /// is that <see cref="Tracked"/> stays the query the write paths use.
    /// </remarks>
    private static async Task<BookingResponse> ToResponseAsync(
        KairosDbContext context,
        Booking booking,
        string? manageToken,
        CancellationToken cancellationToken)
    {
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var start = Instant.FromUnixTimeSeconds(booking.StartUtc);
        var end = Instant.FromUnixTimeSeconds(booking.EndUtc);

        var entry = await context.CalendarEntries.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BookingId == booking.Id, cancellationToken);

        var conference = await context.ConferenceLinks.AsNoTracking()
            .FirstOrDefaultAsync(row => row.BookingId == booking.Id, cancellationToken);

        return new BookingResponse(
            booking.Id,
            booking.Link.Slug,
            InstantPattern.ExtendedIso.Format(start),
            InstantPattern.ExtendedIso.Format(end),
            (int)(end - start).TotalMinutes,
            booking.Status.ToString().ToLowerInvariant(),
            booking.Sequence,
            booker.Name,
            booker.Email,
            booker.TimeZoneId,
            booking.Notes,
            manageToken,
            entry?.State.ToString().ToLowerInvariant(),
            conference?.JoinUrl);
    }

    /// <summary>
    /// Canonicalised rather than trusted. The stored zone is what Part B's invitation is rendered
    /// in, and a typo saved today is a mail that fails to build months later.
    /// </summary>
    private static string ResolveZone(string value) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(value)?.Id
        ?? throw DomainException.Invalid(
            TimeZoneInvalidCode, "'timeZone' must be an IANA zone, such as Europe/Warsaw.");
}
