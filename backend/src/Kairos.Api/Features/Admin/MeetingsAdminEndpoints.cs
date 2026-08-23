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
/// One meeting as the operator's list draws it.
/// </summary>
/// <remarks>
/// The calendar fields are flattened out of Part B's <see cref="CalendarEntry"/> rather than
/// nested, because the row asks one question of it — did this reach a calendar, and if not why —
/// and a nested object would invite the page to start rendering a state machine. There is no
/// provider type here and no vendor word: <see cref="CalendarProvider"/> is the lowercase
/// <see cref="IntegrationProvider"/> name, which is the same string
/// <c>/integrations</c> already answers with.
/// </remarks>
/// <param name="ParticipantTimeZone">
/// The zone the participant read the times in, and never the organizer's. It is on the row
/// because "what time did they think this was" is the question that gets asked after a no-show.
/// </param>
public sealed record MeetingResponse(
    Guid Id,
    string Start,
    string End,
    int DurationMinutes,
    string Status,
    Guid LinkId,
    string LinkName,
    string ParticipantName,
    string ParticipantEmail,
    string ParticipantTimeZone,
    bool CalendarSynced,
    string? CalendarProvider,
    string? CalendarError);

/// <summary>
/// Everything one render of the tab needs, in one round trip.
/// </summary>
/// <remarks>
/// <see cref="Links"/> is the filter's options and is deliberately not affected by the filters:
/// narrowing to one link must not leave the operator with a dropdown holding only that link and
/// no way back. It is the links that have meetings, not every link that exists — a filter offering
/// a value that can only ever return nothing is a filter with dead options in it. That also keeps
/// this tab off the links admin's endpoint, which is another task's and would be a dependency
/// this page does not need.
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
/// The read is one route and the write is another: an operator cancels a meeting here, because
/// the alternative was editing the calendar by hand, which leaves the calendar and Kairos
/// disagreeing about a meeting Kairos is the source of truth for. What is still absent is any
/// meeting Kairos did not create: reading a calendar back is a slice with its own conflict rules
/// and it is not smuggled in behind a list.
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
            await MeetingsAsync(context, request.LinkId, from, to, zone, cancellationToken));
    }

    private static async Task<IReadOnlyList<MeetingResponse>> MeetingsAsync(
        KairosDbContext context,
        Guid? linkId,
        LocalDate? from,
        LocalDate? to,
        DateTimeZone zone,
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
                row.Booker?.Name ?? string.Empty,
                row.Booker?.Email ?? string.Empty,
                row.Booker?.TimeZoneId ?? string.Empty,
                row.Calendar is null
                    ? null
                    : new CalendarFacts(row.Calendar.State, row.Calendar.Provider, row.Calendar.LastError))),
        ];
    }

    /// <summary>
    /// The operator calls a meeting off, with a sentence the invitee reads.
    /// </summary>
    /// <remarks>
    /// The cancellation itself is <see cref="Bookings.BookingCanceller"/>'s, shared with the
    /// invitee's own cancel: the work that belongs to this route is the principal, the reason and
    /// the shape of the answer, not the scheduling.
    ///
    /// No manage token reaches the mailer, and none can — only its SHA-256 is stored, and the
    /// plaintext exists in exactly two requests, neither of them this one. The invitee gets the
    /// cancellation with no manage link on it and the operator's reason in place of it.
    /// </remarks>
    private static async Task<MeetingResponse> CancelAsync(
        Guid id,
        CancelMeetingRequest request,
        KairosDbContext context,
        BookingCanceller canceller,
        CancellationToken cancellationToken)
    {
        var booking = await context.Bookings
            .Include(row => row.Link)
            .Include(row => row.Participants)
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw DomainException.NotFound("Meeting");

        await canceller.CancelAsync(booking, request.Reason, manageToken: null, cancellationToken);

        return await OneAsync(context, booking, cancellationToken);
    }

    /// <summary>
    /// Offers the invitee a new time and holds it until they answer. Nothing about the meeting
    /// changes here — that is the whole design: the invitee agreed to a time, so the operator
    /// proposes and the invitee decides.
    /// </summary>
    /// <remarks>
    /// The instant is run through the same check the public create path uses, so a proposal cannot
    /// land on an hour the link does not offer or inside another meeting's margin. That is what
    /// keeps this feature from being a way to write bookings the availability rules would never
    /// have produced.
    ///
    /// Release, check, re-place — <see cref="BookingsEndpoints.RescheduleAsync"/>'s shape, and it
    /// has to be: a confirmed meeting pads its own slot by the margin, so checking with it still
    /// confirmed refuses the hour directly after it, and "push it back half an hour" is the
    /// commonest thing an operator wants to propose. Every path out re-confirms, the failing ones
    /// included, through the same uncancellable <see cref="BookingsEndpoints.ConfirmAsync"/> the
    /// invitee's reschedule compensates with.
    ///
    /// SAME KNOWN HOLE, and it is not fixed here either. Between the release and the re-place this
    /// booking is Cancelled on disk while nobody has cancelled it, and
    /// <see cref="Bookings.BookingCanceller"/> reads that as "off already" and returns having done
    /// nothing — so a cancel landing in the window answers 200, is undone by the re-confirm, and
    /// leaves the meeting standing with nothing announced and nobody told. It is the window
    /// RescheduleAsync already documents; this route widens it to a second caller rather than
    /// creating it. Closing it means a release distinguishable from a cancellation, which is a
    /// change with its own tests.
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
            // The filtered unique index, which is the mechanism rather than a backstop here: two
            // tabs proposing at once must not produce two holds for one meeting. The insert is
            // dropped and the booking put back on its own, since this save carried both.
            context.Entry(proposal).State = EntityState.Detached;
            await mover.RestoreAsync(booking);
            throw DomainException.Conflict(
                ProposalOpenCode, "A proposal for this meeting is already open.");
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

        return await OneAsync(context, booking, cancellationToken);
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
        var proposal = await context.BookingProposals
            .FirstOrDefaultAsync(
                row => (row.Id == proposalId) && (row.BookingId == id), cancellationToken)
            ?? throw DomainException.NotFound("Proposal");

        if (proposal.State == ProposalState.Open)
        {
            proposal.State = ProposalState.Withdrawn;
            proposal.DecidedAt = clock.GetCurrentInstant().ToUnixTimeSeconds();
            await context.SaveChangesAsync(cancellationToken);
        }

        var booking = await context.Bookings
            .Include(row => row.Link)
            .Include(row => row.Participants)
            .SingleAsync(row => row.Id == id, cancellationToken);

        return await OneAsync(context, booking, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var calendar = await context.CalendarEntries.AsNoTracking()
            .Where(entry => entry.BookingId == booking.Id)
            .OrderByDescending(entry => entry.UpdatedAt)
            .Select(entry => new CalendarFacts(entry.State, entry.Provider, entry.LastError))
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
            booker?.Name ?? string.Empty,
            booker?.Email ?? string.Empty,
            booker?.TimeZoneId ?? string.Empty,
            calendar);
    }

    /// <summary>One construction of a row, so the list and the cancel cannot describe it differently.</summary>
    private static MeetingResponse Describe(
        Guid id,
        long startUtc,
        long endUtc,
        BookingStatus status,
        Guid linkId,
        string linkName,
        string bookerName,
        string bookerEmail,
        string bookerTimeZone,
        CalendarFacts? calendar) =>
        new(
            id,
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(startUtc)),
            InstantPattern.ExtendedIso.Format(Instant.FromUnixTimeSeconds(endUtc)),
            (int)((endUtc - startUtc) / SecondsPerMinute),
            status.ToString().ToLowerInvariant(),
            linkId,
            linkName,
            bookerName,
            bookerEmail,
            bookerTimeZone,
            // Synced is the only state that reads as synced. A missing entry means nobody was
            // connected, Failed means somebody refused, and Pending means a write nothing has
            // finished — all three are the operator's calendar not holding this meeting, which is
            // the only question the row is asking.
            calendar is not null && calendar.State == CalendarSyncState.Synced,
            calendar is null ? null : calendar.Provider.ToString().ToLowerInvariant(),
            calendar?.LastError);

    /// <summary>The calendar half of a row, flattened. One record so the list and the single read
    /// hand <see cref="Describe"/> the same thing.</summary>
    private sealed record CalendarFacts(CalendarSyncState State, IntegrationProvider Provider, string? LastError);

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
