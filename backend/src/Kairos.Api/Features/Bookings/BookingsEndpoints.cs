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
        return group;
    }

    private static async Task<Created<BookingResponse>> CreateAsync(
        CreateBookingRequest request,
        KairosDbContext context,
        SlotQuery slots,
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
        var start = ParseInstant(request.Start);
        var bookerZone = ResolveZone(request.TimeZone);

        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        var offered = await RequireOfferedAsync(
            slots,
            request.Slug,
            request.DurationMinutes,
            start,
            now,
            DateTimeZoneProviders.Tzdb[settings.TimeZoneId],
            cancellationToken);

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
        SlotQuery slots,
        CalendarSyncService calendars,
        BookingMailer mailer,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();
        var start = ParseInstant(request.Start);

        var booking = await FindAsync(context, token, cancellationToken);
        if (booking.Status != BookingStatus.Confirmed)
        {
            throw DomainException.Conflict(BookingCancelledCode, "A cancelled booking cannot be moved.");
        }

        var seconds = booking.EndUtc - booking.StartUtc;
        var settings = await context.Settings.AsNoTracking()
            .SingleAsync(row => row.Id == Settings.SingletonId, cancellationToken);

        // A confirmed booking blocks its own calendar, so it is released before the re-check —
        // otherwise moving a meeting half an hour later is refused by the meeting being moved.
        // Every path out of here puts the row back confirmed, including the failing ones and the
        // ones whose caller has already hung up — which is what ConfirmAsync refusing a token is for.
        //
        // KNOWN HOLE, and it is not fixed here. Between this line and ConfirmAsync the booking is
        // Cancelled on disk while nobody has cancelled it, and BookingCanceller reads that as "off
        // already" and returns having done nothing. A cancel landing in this window therefore
        // answers 200 and is then undone by the re-confirm below: the meeting stands, no
        // announcement was queued, no calendar event was withdrawn and nobody was told. It is old
        // shape and newly reachable, because until the operator's cancel existed the only caller
        // who could hit it was the one invitee already inside this method. Fixing it means a
        // release that is distinguishable from a cancellation — a flag, or a re-read under the
        // write lock — and that is a change with its own tests, not a rider on this one.
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = now.ToUnixTimeSeconds();
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await RequireOfferedAsync(
                slots,
                booking.Link.Slug,
                (int)(seconds / SecondsPerMinute),
                start,
                now,
                DateTimeZoneProviders.Tzdb[settings.TimeZoneId],
                cancellationToken);
        }
        catch
        {
            await ConfirmAsync(context, booking);
            throw;
        }

        booking.StartUtc = start.ToUnixTimeSeconds();
        booking.EndUtc = booking.StartUtc + seconds;

        // The row is mutated rather than replaced: the identifier and the manage token belong to
        // the meeting, not to the slot. Sequence is how Part B's .ics tells a calendar client
        // that this is the same event at a new time instead of a second invitation.
        booking.Sequence++;
        await ConfirmAsync(context, booking);

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

    /// <summary>
    /// Availability, minimum notice and the daily cap enforced on write. <see cref="BookingWriter"/>
    /// only refuses a clash with another confirmed booking, so without this a POST naming 03:00
    /// on a Sunday would succeed against a calendar that never offered it. Returns what the query
    /// resolved, so the write path never has to resolve the link or its margin a second time.
    /// </summary>
    private static async Task<SlotResults> RequireOfferedAsync(
        SlotQuery slots,
        string slug,
        int durationMinutes,
        Instant start,
        Instant now,
        DateTimeZone organizerZone,
        CancellationToken cancellationToken)
    {
        var date = start.InZone(organizerZone).Date;
        var offered = await slots.GetSlotsAsync(slug, durationMinutes, date, date, now, cancellationToken);
        if (!offered.Starts.Contains(start))
        {
            throw DomainException.Conflict(SlotUnavailableCode, "That time is not available.");
        }

        return offered;
    }

    /// <summary>
    /// Deliberately uncancellable, and the reason the release above is safe to commit at all. It
    /// is the compensation for a write this request has already made, so it has to outlive the
    /// request the same way <see cref="Integrations.Google.GoogleClientFactory"/>'s
    /// reconnect flag does: an invitee whose connection drops in the window between the release
    /// and this would otherwise leave the booking cancelled forever — reads reporting a meeting
    /// that is off, reschedule refusing it, and nothing having withdrawn the calendar event or
    /// announced anything, because both of those live in the cancel endpoint and never ran.
    /// </summary>
    private static Task ConfirmAsync(KairosDbContext context, Booking booking)
    {
        booking.Status = BookingStatus.Confirmed;
        booking.CancelledAt = null;
        return context.SaveChangesAsync(CancellationToken.None);
    }

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

    private static Instant ParseInstant(string value)
    {
        var parsed = InstantPattern.ExtendedIso.Parse(value);
        if (!parsed.Success)
        {
            throw DomainException.Invalid(
                InstantInvalidCode, "'start' must be a UTC instant, such as 2026-09-07T09:00:00Z.");
        }

        return parsed.Value;
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
