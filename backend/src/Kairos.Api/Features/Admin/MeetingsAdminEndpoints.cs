using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Features.Bookings;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Shared;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Features.Admin;

/// <summary>
/// The three filters, all optional. Absent means unfiltered rather than empty: the operator's
/// first look at this tab is every meeting there has ever been, and a required range would make
/// "what did I do in July" a thing to construct rather than a thing to ask for.
/// </summary>
public sealed record ListMeetingsRequest
{
    [FromQuery(Name = "linkId")]
    public Guid? LinkId { get; init; }

    /// <summary>
    /// ISO calendar dates in the ORGANIZER's zone, inclusive at both ends — the same vocabulary
    /// <c>GET /links/{slug}/slots</c> speaks, and for the same reason: a day is what the operator
    /// is filtering by, and a day is not an instant until a zone says which one.
    /// </summary>
    [FromQuery(Name = "from")]
    public string? From { get; init; }

    [FromQuery(Name = "to")]
    public string? To { get; init; }
}

/// <summary>
/// Why the operator is calling it off, in their words. The invitee reads it, so it is bounded the
/// way every other free-text field on a request is bounded.
/// </summary>
public sealed record CancelMeetingRequest
{
    /// <summary>
    /// The bound, public so a test can say <c>MaxReasonLength + 1</c> rather than carry its own
    /// copy of the number — the habit <see cref="MeetingsAdminEndpoints.DateInvalidCode"/> and
    /// <see cref="Scheduling.SchedulingDefaults.ProposalHoldHours"/> already keep.
    /// </summary>
    public const int MaxReasonLength = 2000;

    [StringLength(MaxReasonLength)]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Where the operator would like the meeting to move. Its length does not change.</summary>
public sealed record ProposeMeetingRequest
{
    /// <summary>One of the strings <c>GET /links/{slug}/slots</c> returned, verbatim.</summary>
    [Required]
    public required string Start { get; init; }
}

/// <summary>One link, as the filter's option list needs it. Not the link admin's shape.</summary>
public sealed record MeetingLinkResponse(Guid Id, string Name);

/// <summary>
/// The newest proposal on a meeting, whatever became of it, or absent when there was never one.
/// </summary>
/// <remarks>
/// The newest and only the newest, because the row draws one thing: an open hold is a Withdraw
/// button, a closed one at most a sentence. Answering with an older one would print "they declined"
/// over a question the operator is still asking.
/// </remarks>
/// <param name="State">
/// The lower-cased <see cref="ProposalState"/> as of now, with the clock folded in as
/// <see cref="ProposalWriter.StateAt"/> folds it. Never <c>accepted</c> on a row the operator is
/// looking at, since accepting is what moves the meeting.
/// </param>
public sealed record MeetingProposalResponse(Guid Id, string Start, string ExpiresAt, string State);

/// <summary>One meeting as the operator's list draws it.</summary>
/// <remarks>
/// The calendar fields are flattened out of Part B's <see cref="CalendarEntry"/> rather than
/// nested: the row asks it one question — did this reach a calendar, and if not why — and a nested
/// object invites the page to render a state machine. No vendor word either:
/// <see cref="CalendarProvider"/> is the lowercase <see cref="IntegrationProvider"/> name, the
/// string <c>/integrations</c> already answers with.
/// </remarks>
/// <param name="ParticipantTimeZone">
/// The participant's zone, never the organizer's. "What time did they think this was" is the
/// question asked after a no-show.
/// </param>
/// <param name="Slug">
/// The link's slug, beside its id and not instead of it: the row's propose panel reads the times it
/// may offer from <c>GET /links/{slug}/slots</c>, which takes no id at all. Without it the picker
/// cannot ask, and the alternative is a round trip per row to turn an id back into the slug.
/// </param>
/// <param name="Proposal">
/// The newest hold on this meeting, or null when there has never been one. Null is what the row
/// draws Propose off, which is why it is absent rather than a proposal in some resting state.
/// </param>
public sealed record MeetingResponse(
    Guid Id,
    string Start,
    string End,
    int DurationMinutes,
    string Status,
    Guid LinkId,
    string LinkName,
    string Slug,
    string ParticipantName,
    string ParticipantEmail,
    string ParticipantTimeZone,
    bool CalendarSynced,
    string? CalendarProvider,
    string? CalendarError,
    MeetingProposalResponse? Proposal,
    string? ConferenceUrl);

/// <summary>Everything one render of the tab needs, in one round trip.</summary>
/// <remarks>
/// <see cref="Links"/> is the filter's options and deliberately unaffected by the filters:
/// narrowing to one link must not leave the operator a dropdown holding only that link. It lists
/// the links that have meetings, not every link, since an option that can only return nothing is a
/// dead one. That also keeps this tab off the links admin's endpoint.
/// </remarks>
public sealed record MeetingsResponse(
    string TimeZone,
    IReadOnlyList<MeetingLinkResponse> Links,
    IReadOnlyList<MeetingResponse> Meetings);

/// <summary>
/// The operator's read of every meeting Kairos made, and the one fact a calendar cannot supply:
/// which link it came through.
/// </summary>
/// <remarks>
/// The write is a separate route: an operator cancels here because the alternative, editing the
/// calendar by hand, leaves it and Kairos disagreeing about a meeting Kairos owns. Meetings Kairos
/// did not create are still absent — reading a calendar back has its own conflict rules and is not
/// smuggled in behind a list.
/// </remarks>
public static class MeetingsAdminEndpoints
{
    /// <summary>
    /// <c>from</c> or <c>to</c> is not an ISO calendar date. Thrown for the same reason
    /// <see cref="Slots.SlotsEndpoints.DateInvalidCode"/> is: no regular expression rejects
    /// 2026-02-30, so the parse decides or nothing does.
    /// </summary>
    public const string DateInvalidCode = "date_invalid";

    /// <summary><c>to</c> is before <c>from</c>. An empty list would answer it as "no meetings".</summary>
    public const string RangeInvertedCode = "range_inverted";

    /// <summary>A proposal for this meeting is already open and unanswered.</summary>
    public const string ProposalOpenCode = "proposal_open";

    /// <summary>
    /// No relay is configured, so a proposal could be minted and never delivered — and its token
    /// exists nowhere else, so it could never be answered either.
    /// </summary>
    public const string MailUnconfiguredCode = "mail_unconfigured";

    /// <summary>The relay refused it. The hold was handed back rather than left unanswerable.</summary>
    public const string ProposalUndeliverableCode = "proposal_undeliverable";

    private const string FromParameter = "from";
    private const string ToParameter = "to";

    private const int SecondsPerMinute = 60;

    public static RouteGroupBuilder MapMeetingsAdmin(this RouteGroupBuilder group)
    {
        group.MapGet("/meetings", ListAsync).WithName("ListMeetings");
        group.MapPost("/meetings/{id:guid}/cancel", CancelAsync).WithName("CancelMeeting");
        group.MapPost("/meetings/{id:guid}/proposals", ProposeAsync).WithName("ProposeMeeting");
        group.MapDelete("/meetings/{id:guid}/proposals/{proposalId:guid}", WithdrawAsync)
            .WithName("WithdrawProposal");
        return group;
    }

    private static async Task<MeetingsResponse> ListAsync(
        [AsParameters] ListMeetingsRequest request,
        KairosDbContext context,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var from = ParseDate(request.From, FromParameter);
        var to = ParseDate(request.To, ToParameter);
        if (from is { } first && to is { } last && last < first)
        {
            throw DomainException.Invalid(
                RangeInvertedCode, $"'{ToParameter}' is before '{FromParameter}'.");
        }

        // Read rather than assumed, so Task 6's timezone picker changes what this page says
        // without a line of this file changing. It is also stated on screen: a column of times
        // belonging to nobody in particular is a column that books the wrong hour.
        var timeZoneId = await context.Settings.AsNoTracking()
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);

        var zone = DateTimeZoneProviders.Tzdb[timeZoneId];

        return new MeetingsResponse(
            timeZoneId,
            await LinksWithMeetingsAsync(context, cancellationToken),
            await MeetingsAsync(
                context, request.LinkId, from, to, zone, clock.GetCurrentInstant(), cancellationToken));
    }

    private static async Task<IReadOnlyList<MeetingResponse>> MeetingsAsync(
        KairosDbContext context,
        Guid? linkId,
        LocalDate? from,
        LocalDate? to,
        DateTimeZone zone,
        Instant now,
        CancellationToken cancellationToken)
    {
        var query = context.Bookings.AsNoTracking();

        if (linkId is { } link)
        {
            query = query.Where(booking => booking.LinkId == link);
        }

        // Half-open on the far end: `to` is an inclusive DAY, so the bound is the start of the
        // day after it. Taken through AtStartOfDay rather than by adding 86400 seconds, because
        // a day is not always that long — the two disagree twice a year, in the hour the whole
        // scheduler is built to get right.
        if (from is { } first)
        {
            var lower = zone.AtStartOfDay(first).ToInstant().ToUnixTimeSeconds();
            query = query.Where(booking => booking.StartUtc >= lower);
        }

        if (to is { } last)
        {
            var upper = zone.AtStartOfDay(last.PlusDays(1)).ToInstant().ToUnixTimeSeconds();
            query = query.Where(booking => booking.StartUtc < upper);
        }

        var rows = await query
            .OrderBy(booking => booking.StartUtc)
            .ThenBy(booking => booking.Id)
            .Select(booking => new
            {
                booking.Id,
                booking.StartUtc,
                booking.EndUtc,
                booking.Status,
                booking.LinkId,
                LinkName = booking.Link.Name,
                booking.Link.Slug,
                // The same correlated shape the calendar read below uses, and for the same reason:
                // Booking has no navigation to its proposals, and giving it one so a read-only
                // list could use it would be a schema change made by a page. This lands on the
                // index over BookingId. Ordered newest first — see MeetingProposalResponse — with
                // the key breaking a tie, since a proposal minted and withdrawn inside one second
                // is a double-clicked button rather than a hypothetical.
                Proposal = context.BookingProposals
                    .Where(row => row.BookingId == booking.Id)
                    .OrderByDescending(row => row.CreatedAt)
                    .ThenByDescending(row => row.Id)
                    .Select(row => new ProposalFacts(row.Id, row.StartUtc, row.ExpiresAt, row.State))
                    .FirstOrDefault(),
                Booker = booking.Participants
                    .Where(participant => participant.Role == ParticipantRole.Booker)
                    .Select(participant => new { participant.Name, participant.Email, participant.TimeZoneId })
                    .FirstOrDefault(),
                // A correlated read rather than an Include: Booking has no navigation to its
                // calendar entries, and giving it one so a read-only list could use it would be a
                // schema change made by a page. The index on (BookingId, Provider) is what this
                // lands on. One provider today, so one row; ordered anyway, so the newest wins
                // rather than whichever the file happened to return first.
                Calendar = context.CalendarEntries
                    .Where(entry => entry.BookingId == booking.Id)
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .Select(entry => new { entry.State, entry.Provider, entry.LastError })
                    .FirstOrDefault(),
                Conference = context.ConferenceLinks
                    .Where(conference => conference.BookingId == booking.Id)
                    .OrderByDescending(conference => conference.CreatedAt)
                    .Select(conference => conference.JoinUrl)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => Describe(
                row.Id,
                row.StartUtc,
                row.EndUtc,
                row.Status,
                row.LinkId,
                row.LinkName,
                row.Slug,
                row.Booker?.Name ?? string.Empty,
                row.Booker?.Email ?? string.Empty,
                row.Booker?.TimeZoneId ?? string.Empty,
                row.Calendar is null
                    ? null
                    : new CalendarFacts(row.Calendar.State, row.Calendar.Provider, row.Calendar.LastError),
                row.Proposal,
                row.Conference,
                now)),
        ];
    }

    /// <summary>The operator calls a meeting off, with a sentence the invitee reads.</summary>
    /// <remarks>
    /// The cancellation is <see cref="Bookings.BookingCanceller"/>'s, shared with the invitee's own
    /// cancel; this route owns the principal, the reason and the shape of the answer.
    ///
    /// No manage token reaches the mailer and none can — only its SHA-256 is stored. The invitee
    /// gets the cancellation with the operator's reason in place of a manage link.
    /// </remarks>
    private static async Task<MeetingResponse> CancelAsync(
        Guid id,
        CancelMeetingRequest request,
        KairosDbContext context,
        BookingCanceller canceller,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var booking = await context.Bookings
            .Include(row => row.Link)
            .Include(row => row.Participants)
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Meeting");

        await canceller.CancelAsync(booking, request.Reason, manageToken: null, cancellationToken);

        return await OneAsync(context, booking, clock.GetCurrentInstant(), cancellationToken);
    }

    /// <summary>
    /// Offers the invitee a new time and holds it until they answer. The meeting itself does not
    /// change: the invitee agreed to a time, so the operator proposes and the invitee decides.
    /// </summary>
    /// <remarks>
    /// The instant goes through the check the public create path uses, so a proposal cannot land on
    /// an hour the link does not offer or inside another meeting's margin.
    ///
    /// Release, check, re-place — <see cref="BookingsEndpoints.RescheduleAsync"/>'s shape: a
    /// confirmed meeting pads its own slot by the margin, so checking with it still confirmed
    /// refuses the hour directly after it. Every path out re-confirms through the same uncancellable
    /// <see cref="BookingsEndpoints.ConfirmAsync"/>, the failing ones included.
    ///
    /// SAME KNOWN HOLE, not fixed here: between the release and the re-place the booking is
    /// Cancelled on disk with nobody having cancelled it, so a cancel landing there answers 200 and
    /// is undone by the re-confirm, leaving the meeting standing with nothing announced.
    /// <see cref="Bookings.BookingMover"/> has it in full; this route widens it to a second caller.
    /// </remarks>
    private static async Task<MeetingResponse> ProposeAsync(
        Guid id,
        ProposeMeetingRequest request,
        KairosDbContext context,
        BookingMover mover,
        ProposalWriter proposals,
        BookingMailer mailer,
        IOptions<EmailOptions> email,
        IClock clock,
        CancellationToken cancellationToken)
    {
        // Before anything is read and well before anything is released. A proposal is a question,
        // and the only channel that can carry it is the mail — its token exists nowhere but inside
        // this call. Minting one on a deployment that cannot send is minting a hold that nobody
        // can ever lift, so the honest answer is to refuse rather than to swallow.
        if (!email.Value.IsConfigured)
        {
            throw DomainException.Conflict(
                MailUnconfiguredCode,
                $"A new time cannot be suggested until {EmailOptions.SectionName} is configured: "
                + "the invitee is asked by email, and there is no other way to ask them.");
        }

        var now = clock.GetCurrentInstant();
        var start = BookingMover.ParseInstant(request.Start);

        var booking = await context.Bookings
            .Include(row => row.Link)
            .Include(row => row.Participants)
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Meeting");

        BookingMover.RequireMovable(booking);

        // A hold that has lapsed is holding nothing, and every conflict reader already reads it
        // that way. Here it has to be CLOSED rather than read past, because what blocks the next
        // suggestion is the filtered unique index over BookingId WHERE State = Open — a row, which
        // no query can fold a clock into. Nothing is saved by this; it commits with the release
        // below, and if the release never happens the row is exactly as dead as it already was.
        await proposals.ExpireLapsedAsync(id, now, cancellationToken);

        // Asked before the release rather than left to the index below. A double-submitted form is
        // the ordinary way this arrives, and answering it without ever setting the booking
        // Cancelled keeps it out of the window that release opens — the one where a cancel landing
        // at the same moment reads the meeting as already off and quietly does nothing.
        //
        // The clock is in this read as well as in the close above, and the two are not one check
        // written twice: this one runs against the database, where the close is still uncommitted,
        // so a lapsed hold is refused by the row it can still see unless the read discounts it.
        if (await HasOpenProposalAsync(context, id, now, cancellationToken))
        {
            throw DomainException.Conflict(
                ProposalOpenCode, "A proposal for this meeting is already open.");
        }

        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        await mover.ReleaseAndCheckAsync(booking, start, now, compensate: null, cancellationToken);

        var (proposal, token) = proposals.Add(booking, start, settings.ProposalHoldHours);

        try
        {
            // The restore carries the insert with it. A hold is invisible while the booking under
            // it is not confirmed, so committing these apart would leave a window in which the
            // slot just promised to the invitee is held by nothing at all. The booking goes back
            // where it was rather than moving: proposing is asking, and asking moves nothing.
            await mover.RestoreAsync(booking);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueViolation(exception))
        {
            // The filtered unique index, and now genuinely the backstop: the check above answers
            // the ordinary case, so reaching here means two requests were inside this method
            // together. The insert is dropped and the booking put back on its own, since the failed
            // save carried both. Detaching the proposal alone suffices only because Booking has no
            // navigation to its proposals — add one and EF fixes it up on detach, and the row rides
            // along on the restore's save.
            context.Entry(proposal).State = EntityState.Detached;
            await mover.RestoreAsync(booking);

            // WHICH index fired matters, because this method can violate two. The filtered one on
            // BookingId means somebody proposed first; the one on TokenHash means 32 random bytes
            // repeated, and reporting that as "a proposal is already open" would send the operator
            // looking for a proposal that does not exist.
            if (!await HasOpenProposalAsync(context, id, now, cancellationToken))
            {
                throw;
            }

            throw DomainException.Conflict(
                ProposalOpenCode, "A proposal for this meeting is already open.", exception);
        }

        // After the commit, so nothing mails a proposal that does not exist. The plaintext token
        // is in hand only here, for the length of this call — the database holds its SHA-256,
        // which is exactly why the send happens on this thread rather than behind the outbox.
        if (!await mailer.SendProposalAsync(booking, proposal, token, cancellationToken))
        {
            // And exactly why a swallowed failure cannot stand here. The hold is committed, the
            // token is already gone, and there is no second chance to reach the invitee — so the
            // slot goes straight back and the operator is told. Uncancellable for ConfirmAsync's
            // reason: this compensates a write the request has already made, and a caller who hung
            // up must not leave a hold standing that nothing can ever answer.
            proposal.State = ProposalState.Withdrawn;
            proposal.DecidedAt = now.ToUnixTimeSeconds();
            await context.SaveChangesAsync(CancellationToken.None);

            throw DomainException.Conflict(
                ProposalUndeliverableCode,
                "The suggestion could not be sent, so nothing is being held. Try again.");
        }

        return await OneAsync(context, booking, now, cancellationToken);
    }

    /// <summary>
    /// Whether a hold is open AND still holding. Both halves, and the clock is not decoration:
    /// without it a lapsed row that nothing had swept still answered
    /// <see cref="ProposalOpenCode"/>, which left the operator unable to suggest a new time until
    /// a tick fired — the one thing in this feature that waited on the sweeper, against a design
    /// whose whole claim is that nothing does.
    /// </summary>
    private static Task<bool> HasOpenProposalAsync(
        KairosDbContext context,
        Guid bookingId,
        Instant now,
        CancellationToken cancellationToken)
    {
        // Hoisted, because NodaTime's conversion does not translate inside an expression tree.
        var seconds = now.ToUnixTimeSeconds();

        return context.BookingProposals.AsNoTracking()
            .AnyAsync(
                row => (row.BookingId == bookingId)
                    && (row.State == ProposalState.Open)
                    && (row.ExpiresAt > seconds),
                cancellationToken);
    }

    /// <summary>
    /// Takes a proposal back. Without it, an operator who names the wrong time holds a slot
    /// hostage for the whole hold and cannot offer the right one, because only one proposal may be
    /// open at a time.
    /// </summary>
    private static async Task<MeetingResponse> WithdrawAsync(
        Guid id,
        Guid proposalId,
        KairosDbContext context,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var exists = await context.BookingProposals.AsNoTracking()
            .AnyAsync(row => (row.Id == proposalId) && (row.BookingId == id), cancellationToken);

        if (!exists)
        {
            throw DomainException.NotFound("Proposal");
        }

        // Conditional on the row still being Open rather than on a read a statement earlier, so an
        // invitee answering at the same moment cannot have their answer written over by this. The
        // count is deliberately not checked: withdrawing something already closed is what a
        // double-clicked button asks for, and the honest answer to it is the row as it stands.
        //
        // Which closed state it lands in is decided by the row's own expiry rather than by the
        // button that was pressed, and the same fold every other reader applies. The operator sees
        // Withdraw on a hold that lapsed between the page rendering and the click; writing that as
        // Withdrawn would say they took back a live suggestion — and would drop the lapse notice,
        // because the sweeper looks for Expired rows nobody has been told about. NotifiedAt stays
        // null here for exactly that reason.
        var now = clock.GetCurrentInstant();
        var decidedAt = now.ToUnixTimeSeconds();
        await context.BookingProposals
            .Where(row => (row.Id == proposalId) && (row.State == ProposalState.Open))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(
                        row => row.State,
                        row => row.ExpiresAt <= decidedAt ? ProposalState.Expired : ProposalState.Withdrawn)
                    .SetProperty(row => row.DecidedAt, decidedAt),
                cancellationToken);

        var booking = await context.Bookings.AsNoTracking()
            .Include(row => row.Link)
            .Include(row => row.Participants)
            .SingleAsync(row => row.Id == id, cancellationToken);

        return await OneAsync(context, booking, now, cancellationToken);
    }

    /// <summary>
    /// One meeting in the shape the list draws it, for the route that changes one. Written as a
    /// read of the same fields rather than as a second projection: a write that answered in a
    /// different shape from the list would be a second description of a meeting, and the table
    /// would start disagreeing with the row it just refreshed.
    /// </summary>
    private static async Task<MeetingResponse> OneAsync(
        KairosDbContext context,
        Booking booking,
        Instant now,
        CancellationToken cancellationToken)
    {
        var calendar = await context.CalendarEntries.AsNoTracking()
            .Where(entry => entry.BookingId == booking.Id)
            .OrderByDescending(entry => entry.UpdatedAt)
            .Select(entry => new CalendarFacts(entry.State, entry.Provider, entry.LastError))
            .FirstOrDefaultAsync(cancellationToken);

        // Read back rather than taken from the write that just happened: propose has an insert in
        // hand and withdraw has an id, but cancel has neither and all three answer in one shape.
        // A route that described the proposal from what it wrote would be the second description
        // of a meeting this method exists to prevent.
        var proposal = await context.BookingProposals.AsNoTracking()
            .Where(row => row.BookingId == booking.Id)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Select(row => new ProposalFacts(row.Id, row.StartUtc, row.ExpiresAt, row.State))
            .FirstOrDefaultAsync(cancellationToken);

        var conference = await context.ConferenceLinks.AsNoTracking()
            .Where(entry => entry.BookingId == booking.Id)
            .OrderByDescending(entry => entry.CreatedAt)
            .Select(entry => entry.JoinUrl)
            .FirstOrDefaultAsync(cancellationToken);

        var booker = booking.Participants
            .FirstOrDefault(participant => participant.Role == ParticipantRole.Booker);

        return Describe(
            booking.Id,
            booking.StartUtc,
            booking.EndUtc,
            booking.Status,
            booking.LinkId,
            booking.Link.Name,
            booking.Link.Slug,
            booker?.Name ?? string.Empty,
            booker?.Email ?? string.Empty,
            booker?.TimeZoneId ?? string.Empty,
            calendar,
            proposal,
            conference,
            now);
    }

    /// <summary>One construction of a row, so the list and the cancel cannot describe it differently.</summary>
    private static MeetingResponse Describe(
        Guid id,
        long startUtc,
        long endUtc,
        BookingStatus status,
        Guid linkId,
        string linkName,
        string slug,
        string bookerName,
        string bookerEmail,
        string bookerTimeZone,
        CalendarFacts? calendar,
        ProposalFacts? proposal,
        string? conferenceUrl,
        Instant now) =>
        new(
            id,
            Format(startUtc),
            Format(endUtc),
            (int)((endUtc - startUtc) / SecondsPerMinute),
            status.ToString().ToLowerInvariant(),
            linkId,
            linkName,
            slug,
            bookerName,
            bookerEmail,
            bookerTimeZone,
            // Synced is the only state that reads as synced. A missing entry means nobody was
            // connected, Failed means somebody refused, and Pending means a write nothing has
            // finished — all three are the operator's calendar not holding this meeting, which is
            // the only question the row is asking.
            calendar is not null && calendar.State == CalendarSyncState.Synced,
            calendar is null ? null : calendar.Provider.ToString().ToLowerInvariant(),
            calendar?.LastError,
            proposal is null
                ? null
                : new MeetingProposalResponse(
                    proposal.Id,
                    Format(proposal.StartUtc),
                    Format(proposal.ExpiresAt),
                    ProposalWriter.StateAt(proposal.State, proposal.ExpiresAt, now)
                        .ToString().ToLowerInvariant()),
            conferenceUrl);

    private static string Format(long unixSeconds) =>
        InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(unixSeconds));

    private sealed record CalendarFacts(CalendarSyncState State, IntegrationProvider Provider, string? LastError);

    /// <summary>
    /// The hold half of a row, as far as the projection reads it. The stored state rather than the
    /// folded one — the fold needs a clock, and a clock read inside an EF projection is a clock
    /// read the database is being asked to translate.
    /// </summary>
    private sealed record ProposalFacts(Guid Id, long StartUtc, long ExpiresAt, ProposalState State);

    private static async Task<IReadOnlyList<MeetingLinkResponse>> LinksWithMeetingsAsync(
        KairosDbContext context,
        CancellationToken cancellationToken)
    {
        var links = await context.Bookings.AsNoTracking()
            .Select(booking => new { booking.LinkId, Name = booking.Link.Name })
            .Distinct()
            .OrderBy(link => link.Name)
            .ToListAsync(cancellationToken);

        return [.. links.Select(link => new MeetingLinkResponse(link.LinkId, link.Name))];
    }

    /// <summary>
    /// Absent is not a failure — every filter here is optional. The caller's value is never echoed
    /// back into the message, the same habit <see cref="Slots.SlotsEndpoints"/> keeps.
    /// </summary>
    private static LocalDate? ParseDate(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parsed = LocalDatePattern.Iso.Parse(value);
        if (!parsed.Success)
        {
            throw DomainException.Invalid(
                DateInvalidCode, $"'{parameter}' must be an ISO calendar date, such as 2026-09-07.");
        }

        return parsed.Value;
    }
}
