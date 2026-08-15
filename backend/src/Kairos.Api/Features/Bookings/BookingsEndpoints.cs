using System.ComponentModel.DataAnnotations;

using Kairos.Api.Availability;
using Kairos.Api.Integrations;
using Kairos.Api.Integrations.Email;
using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
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
        group.MapPost("/bookings", CreateAsync).WithName("CreateBooking");
        group.MapGet("/bookings/{token}", GetAsync).WithName("GetBooking");
        group.MapPost("/bookings/{token}/cancel", CancelAsync).WithName("CancelBooking");
        group.MapPost("/bookings/{token}/reschedule", RescheduleAsync).WithName("RescheduleBooking");
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
    /// Idempotent: a manage link opened twice, or a double-clicked button, gets the same answer
    /// rather than a 404 for a booking the caller can plainly see.
    /// </summary>
    /// <remarks>
    /// The <c>booking.cancelled</c> announcement is enqueued here, in this endpoint, and
    /// deliberately not wherever <see cref="Booking.Status"/> becomes
    /// <see cref="BookingStatus.Cancelled"/>. <see cref="RescheduleAsync"/> is release, re-check,
    /// re-place: it sets a booking cancelled and back to confirmed on every path out, so a hook
    /// on the status change would announce a cancellation for every meeting that merely moved.
    /// This endpoint is the only place where cancelled is where the booking stays.
    /// </remarks>
    private static async Task<BookingResponse> CancelAsync(
        string token,
        KairosDbContext context,
        OutboxWriter outbox,
        CalendarSyncService calendars,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var booking = await FindAsync(context, token, cancellationToken);
        if (booking.Status != BookingStatus.Confirmed)
        {
            return await ToResponseAsync(context, booking, null, cancellationToken);
        }

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = clock.GetCurrentInstant().ToUnixTimeSeconds();

        // One SaveChanges, so the cancellation and the announcement of it commit together or
        // neither does — the same rule BookingWriter follows for creation, for the same reason.
        outbox.Enqueue(
            booking.Id,
            OutboxMessageType.BookingCancelled,
            BookingEventPayload.Cancelled(
                booking.Id,
                booking.Link.Slug,
                Instant.FromUnixTimeSeconds(booking.StartUtc),
                Instant.FromUnixTimeSeconds(booking.EndUtc)).ToJson());

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueViolation(exception))
        {
            // Two cancels in flight at once — the double-clicked button this endpoint is
            // documented to survive. The other one committed first, so the booking is cancelled
            // and its announcement is queued; this call has nothing left to do but agree. Without
            // this catch, adding the outbox row would have turned an idempotent 200 into a 500.
            //
            // Returned from here rather than falling through, for a reason the withdrawal below
            // makes concrete: the outbox row this save could not write is still tracked, so the
            // sync service's own SaveChanges would raise the same violation again and lose the
            // state change it was making. The other call is withdrawing the event anyway.
            return await ToResponseAsync(context, booking, null, cancellationToken);
        }

        // After the cancellation is committed, so nothing withdraws an event for a booking that
        // is still standing. It cannot fail this request — CancelAsync has no throwing path —
        // and a Google that refuses leaves a Failed row rather than a 500.
        await calendars.CancelAsync(booking.Id, cancellationToken);

        return await ToResponseAsync(context, booking, null, cancellationToken);
    }

    private static async Task<BookingResponse> RescheduleAsync(
        string token,
        RescheduleBookingRequest request,
        KairosDbContext context,
        SlotQuery slots,
        CalendarSyncService calendars,
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
        // Every path out of here puts the row back confirmed, including the failing ones.
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
            await ConfirmAsync(context, booking, cancellationToken);
            throw;
        }

        booking.StartUtc = start.ToUnixTimeSeconds();
        booking.EndUtc = booking.StartUtc + seconds;

        // The row is mutated rather than replaced: the identifier and the manage token belong to
        // the meeting, not to the slot. Sequence is how Part B's .ics tells a calendar client
        // that this is the same event at a new time instead of a second invitation.
        booking.Sequence++;
        await ConfirmAsync(context, booking, cancellationToken);

        // Here, at the end, and never in the release phase above. That release is bookkeeping —
        // the booking is set cancelled and put back confirmed on every path out of this method,
        // the failing ones included — so a calendar hook hung on the status change would withdraw
        // the event for every meeting that merely moved, and an attendee who had already accepted
        // would watch it vanish. Exactly the trap the booking.cancelled announcement is kept out
        // of, and pinned by the same pair of tests.
        await calendars.RescheduleAsync(booking, cancellationToken);

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

    private static Task ConfirmAsync(
        KairosDbContext context,
        Booking booking,
        CancellationToken cancellationToken)
    {
        booking.Status = BookingStatus.Confirmed;
        booking.CancelledAt = null;
        return context.SaveChangesAsync(cancellationToken);
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
