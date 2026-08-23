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
/// exactly once, in the 201 from create: after that only its hash exists.
/// </summary>
/// <param name="CalendarSync">
/// Where this booking is in the organizer's calendar, lower-cased from
/// <see cref="CalendarSyncState"/>. Null means nothing was ever attempted, the ordinary state with
/// no calendar connected. No provider error text: it is unfiltered third-party prose routinely
/// carrying the organizer's account address, and an invitee's manage token can read this response.
/// </param>
/// <param name="ConferenceUrl">
/// Where to join, or null if this meeting has no second channel. The invitee already has it in
/// their invitation; it is here so a page can show it without one.
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
/// <param name="TimeZone">
/// The BOOKER's own zone, as they gave it, and never the organizer's — the same field
/// <see cref="BookingResponse.TimeZone"/> carries and for the same reason. This is the third
/// surface an invitee sees about one booking: the proposal mail formats against
/// <c>booker.TimeZoneId</c>, the manage page renders <c>booking.timeZone</c>, and a page that
/// answered the organizer's here would print a different clock time for the same instant in the
/// tab opened from that mail.
/// </param>
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
        // knows about, and never before the write committed. Sent on this thread rather than from a
        // queue because the plaintext manage token the confirmation carries exists only here, in
        // memory, and a queue would have to store it. The mailer swallows its own failures, so a
        // relay being down cannot become a booking the organizer never gets.
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
    /// Everything that happens to the booking is <see cref="BookingCanceller"/>'s, shared with the
    /// operator's cancel in <see cref="Admin.MeetingsAdminEndpoints"/>. No reason is passed: the
    /// null column is what records that this was the invitee's cancellation, not the operator's.
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

        // Here, at the end, and never in the release phase above: that release is bookkeeping the
        // failing paths undo, so a calendar hook hung on the status change would withdraw the event
        // for every meeting that merely moved and an attendee who had already accepted would watch
        // it vanish. The trap the booking.cancelled announcement is kept out of, same pair of tests.
        await calendars.RescheduleAsync(booking, cancellationToken);

        // Then the invitee, so the mail describes a calendar the organizer's side already agrees
        // with. The token arrived in the URL, stays in memory for this request and is never
        // persisted. Sequence was advanced above and the UID is the booking id, which together are
        // what make this land as a change rather than as a second meeting.
        await mailer.SendRescheduleAsync(booking, token, cancellationToken);

        return await ToResponseAsync(context, booking, null, cancellationToken);
    }

    private static async Task<ProposalResponse> GetProposalAsync(
        string token,
        KairosDbContext context,
        IClock clock,
        CancellationToken cancellationToken) =>
        Describe(
            await FindProposalAsync(context, token, cancellationToken),
            clock.GetCurrentInstant());

    /// <summary>The invitee agrees, and only now does anything become true.</summary>
    /// <remarks>
    /// Release, re-check, re-place — <see cref="RescheduleAsync"/>'s shape and for its reason: a
    /// confirmed booking blocks its own calendar, so it is released before the re-check.
    ///
    /// The proposal is closed in that same save, and no exclusion parameter exists anywhere: a row
    /// that is no longer Open is invisible to both conflict readers. That is not what carries the
    /// re-check — both readers also require a hold's booking to be Confirmed, so the release alone
    /// already hides this hold. Closing it buys what comes after: at the re-place a still-open hold
    /// would block the slot the meeting now occupies, refuse every later proposal through the
    /// filtered unique index, and reach the sweeper as a suggestion nobody answered.
    ///
    /// Every path out puts the booking back confirmed. A proposal closed by a failed re-check goes
    /// to Withdrawn rather than Open: the slot it was holding is gone.
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

        await mover.ReleaseAndCheckAsync(
            booking,
            start,
            now,
            compensate: () =>
                CloseIfOpenAsync(context, proposal, ProposalState.Withdrawn, now, CancellationToken.None),
            cancellationToken);

        try
        {
            // After the check and before the move, which is the only placement where both rules
            // hold. Earlier, and every failure in the released window — a provider falling over,
            // an invitee closing the tab — would leave the row answered when nobody answered it.
            // Later is not possible at all: the move must not commit for a suggestion somebody
            // else has already decided.
            await ClaimAsync(context, proposal, ProposalState.Accepted, now, cancellationToken);
        }
        catch
        {
            // The claim was lost, so this request moves nothing and owes the booking its slot back.
            // The proposal is left exactly as the winner wrote it.
            await mover.RestoreAsync(booking);
            throw;
        }

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

    /// <summary>The invitee says no.</summary>
    /// <remarks>
    /// "A declined proposal leaves the meeting exactly as it was" holds by construction: the
    /// booking is read through <see cref="FindProposalAsync"/>, which is AsNoTracking, so nothing
    /// on this path has a tracked booking to write, and its one write is a targeted UPDATE against
    /// the proposal row.
    /// </remarks>
    private static async Task<ProposalResponse> DeclineProposalAsync(
        string token,
        KairosDbContext context,
        OperatorMailer operators,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var proposal = await FindProposalAsync(context, token, cancellationToken);
        RequireOpen(proposal, now);
        await ClaimAsync(context, proposal, ProposalState.Declined, now, cancellationToken);

        var timeZoneId = await OrganizerZoneAsync(context, cancellationToken);

        // After the claim and never before it. The claim is what decides whether this request is
        // the one that answered, and a mail sent ahead of it would tell the operator about a
        // refusal that another caller had already turned into an acceptance.
        //
        // Uncancellable, for the reason CloseIfOpenAsync is: the decline is committed by the time
        // this runs. The mailer's rule-14 swallow is guarded by the caller's token, so an invitee
        // who closes the tab would push the failure back out through here instead — and there is
        // no sweep to catch it, since the sweep only ever looks at holds that lapsed.
        await operators.SendAsync(
            proposal.Booking, proposal, OperatorNotice.Declined, timeZoneId, CancellationToken.None);

        return Describe(proposal, now);
    }

    /// <summary>Closes the proposal, and refuses if somebody else got there first.</summary>
    /// <remarks>
    /// The predicate is on the UPDATE rather than on a row read a moment earlier. Accept and
    /// decline are check-then-write: two in flight both read Open and both go on, which is a meeting
    /// moved under a proposal recorded Declined, or moved twice with two mails and two calendar
    /// syncs. Zero rows affected means somebody answered between the read and here.
    ///
    /// The backstop rather than the mechanism, as <see cref="BookingWriter"/>'s partial unique index
    /// is: <see cref="RequireOpen"/> refuses the ordinary case earlier and says WHICH refusal it is,
    /// which this cannot. This covers only the overlap no sequential check can see.
    /// </remarks>
    private static async Task ClaimAsync(
        KairosDbContext context,
        BookingProposal proposal,
        ProposalState state,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (!await CloseIfOpenAsync(context, proposal, state, now, cancellationToken))
        {
            throw DomainException.Conflict(
                ProposalClosedCode, "This suggestion has already been answered.");
        }
    }

    /// <summary>
    /// Moves the row out of Open in one statement, and says whether it was this caller that did it.
    /// </summary>
    /// <remarks>
    /// Uncancellable: both callers compensate or complete a write the request has already
    /// committed. Losing to another caller is not an error — a decline that landed first stands.
    /// </remarks>
    private static async Task<bool> CloseIfOpenAsync(
        KairosDbContext context,
        BookingProposal proposal,
        ProposalState state,
        Instant now,
        CancellationToken cancellationToken)
    {
        var decidedAt = now.ToUnixTimeSeconds();
        var closed = await context.BookingProposals
            .Where(row => (row.Id == proposal.Id) && (row.State == ProposalState.Open))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.State, state)
                    .SetProperty(row => row.DecidedAt, decidedAt),
                cancellationToken);

        if (closed == 0)
        {
            return false;
        }

        // The read copy is untracked, so nothing brings it into line on its own — and a response
        // composed after this has to describe the row that is now on disk.
        proposal.State = state;
        proposal.DecidedAt = decidedAt;
        return true;
    }

    /// <summary>
    /// Open and unlapsed, or nothing happens. The two refusals are separate codes because they
    /// are separate sentences on the page: somebody already answered this, against nobody
    /// answered it in time.
    /// </summary>
    private static void RequireOpen(BookingProposal proposal, Instant now)
    {
        // Through StateAt rather than comparing ExpiresAt here, which is what this used to do.
        // That comparison was a second copy of the fold, four hundred lines from the one whose own
        // docstring says two copies drift — and it had already drifted in one direction: a hold
        // the sweeper HAD closed answered proposal_closed while an identical unswept one answered
        // proposal_expired, so which sentence the invitee read depended on whether a background
        // service had been round yet. Now the state decides and the sweeper cannot change it.
        var state = ProposalWriter.StateAt(proposal.State, proposal.ExpiresAt, now);

        if (state == ProposalState.Expired)
        {
            throw DomainException.Conflict(ProposalExpiredCode, "This suggestion has lapsed.");
        }

        if (state != ProposalState.Open)
        {
            throw DomainException.Conflict(ProposalClosedCode, "This suggestion has already been answered.");
        }
    }

    /// <summary>
    /// By hash against the unique index; a token that matches nothing is a 404 whatever its shape,
    /// the same answer a real token for a deleted proposal gets.
    /// </summary>
    /// <remarks>
    /// Untracked, rule 8: every write to a proposal on these routes is its own conditional UPDATE,
    /// so nothing needs a tracked row and nothing can accidentally save the booking in the Include.
    ///
    /// The participants ride along for two readers now — <see cref="BookerZoneOf"/>, which every
    /// response here is rendered in, and <see cref="OperatorMailer"/>, which names the invitee.
    /// The second is the one that fails quietly: an absent collection makes the composer throw
    /// inside the mailer's own rule-14 swallow, so the decline still succeeds and the operator is
    /// simply never told.
    /// </remarks>
    private static async Task<BookingProposal> FindProposalAsync(
        KairosDbContext context,
        string token,
        CancellationToken cancellationToken)
    {
        var hash = BookingWriter.Hash(token);
        return await context.BookingProposals.AsNoTracking()
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Link)
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Participants)
            .FirstOrDefaultAsync(proposal => proposal.TokenHash == hash, cancellationToken)
            ?? throw DomainException.NotFound("Proposal");
    }

    /// <summary>
    /// The proposal as the invitee's page draws it, in the invitee's own zone — see
    /// <see cref="ProposalResponse.TimeZone"/> for why it is theirs and not the organizer's. Taken
    /// off the booking this proposal already carries rather than read separately, so it cannot
    /// disagree with the mail that sent them here.
    /// </summary>
    private static ProposalResponse Describe(BookingProposal proposal, Instant now) =>
        new(
            proposal.BookingId,
            proposal.Booking.Link.Name,
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.Booking.StartUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.Booking.EndUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.StartUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.EndUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(proposal.ExpiresAt)),
            BookerZoneOf(proposal.Booking),
            StateOf(proposal, now));

    /// <summary>
    /// The organizer's own zone, out of the settings row.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="BookerZoneOf"/>, which is the invitee's and is what the page
    /// above renders. The only reader of this is the mail to the operator's own mailbox, and every
    /// message in Kairos is written in the zone of whoever it is addressed to.
    /// </remarks>
    private static async Task<string> OrganizerZoneAsync(
        KairosDbContext context,
        CancellationToken cancellationToken) =>
        await context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// The zone the booker gave. Empty when a booking somehow carries no booker, which the page
    /// answers by falling back rather than by failing — <c>Intl</c> refuses an empty zone and the
    /// whole page would go down over one missing row.
    /// </summary>
    private static string BookerZoneOf(Booking booking) =>
        booking.Participants
            .FirstOrDefault(participant => participant.Role == ParticipantRole.Booker)
            ?.TimeZoneId ?? string.Empty;

    /// <summary>
    /// The state as every other reader computes it — <see cref="ProposalWriter.StateAt"/> — as the
    /// lower-cased word the page branches on. Answering "open" for a hold that has stopped holding
    /// is a page that draws Accept and Decline over a suggestion that will refuse either.
    /// </summary>
    private static string StateOf(BookingProposal proposal, Instant now) =>
        ProposalWriter.StateAt(proposal.State, proposal.ExpiresAt, now).ToString().ToLowerInvariant();


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
    /// Two reads rather than a join: both tables are keyed by booking id and read once per response
    /// on an endpoint nobody hits in a loop, so two indexed lookups buy keeping
    /// <see cref="Tracked"/> as the query the write paths use.
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
