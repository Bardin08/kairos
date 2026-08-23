using System.Security.Cryptography;
using System.Text;

using Kairos.Api.Integrations.Webhooks;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// The booking write. Everything else in the codebase may be wrong and be a bug; this being
/// wrong is a double-booked calendar, so it is the one place that talks to SQLite directly.
/// </summary>
public sealed class BookingWriter(KairosDbContext context, IClock clock, OutboxWriter outbox)
{
    /// <summary>
    /// The slot is not available: it conflicts with a confirmed booking, or with the hold an open
    /// proposal has on it. The only failure this returns, and one code for both because the caller
    /// can do nothing different about either — the slot is gone and another has to be picked.
    /// </summary>
    public const string SlotTakenReason = "slot_taken";

    private const int ManageTokenBytes = 32;

    private const int SecondsPerMinute = 60;

    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE. The primary code, 19, covers UNIQUE, FOREIGN KEY, NOT NULL and
    /// CHECK alike, so matching on it would report an unknown link or a malformed booker as
    /// "that slot was taken" and lose the real cause. Only a unique index means taken.
    /// </summary>
    private const int SqliteConstraintUniqueErrorCode = 2067;

    /// <summary>
    /// What a link inherits when the settings singleton is missing. Without it the row drops out
    /// of the join, the count comes back zero and every booking is accepted — a missing
    /// configuration row would silently disable the one check this class exists to perform.
    /// </summary>
    private const int NoMarginFallback = 0;

    private static readonly int ConfirmedStatus = (int)BookingStatus.Confirmed;

    private static readonly int OpenProposalState = (int)ProposalState.Open;

    /// <summary>
    /// EF's BeginTransaction issues a DEFERRED transaction, which takes a write lock only when it
    /// first writes. Two deferred transactions that both read then write deadlock in a way
    /// busy_timeout cannot rescue, because SQLite returns SQLITE_BUSY immediately when waiting
    /// could never resolve it. BEGIN IMMEDIATE takes the write lock up front, which is what makes
    /// check-then-insert correct here. This is the only raw SQL in the codebase.
    /// </summary>
    /// <param name="slug">
    /// The link's public name, for the <c>booking.created</c> body. Passed in rather than looked
    /// up because the caller resolved the link a moment ago and a second read inside the write
    /// transaction would be the same fact fetched twice.
    /// </param>
    public CreateBookingResult TryCreate(
        Guid linkId,
        string slug,
        Instant start,
        Duration duration,
        BookerDetails booker,
        string notes,
        int queryingMargin)
    {
        var startUtc = start.ToUnixTimeSeconds();
        var endUtc = (start + duration).ToUnixTimeSeconds();

        // Opening the connection here is not only about the pragmas: a connection EF opened for
        // itself is one EF closes after each command, and a closed connection is a rolled-back
        // BEGIN IMMEDIATE. Opened from outside, EF leaves it alone until this method is done.
        SqlitePragmas.Apply((SqliteConnection)context.Database.GetDbConnection());

        // A booking is two inserts, and EF wraps a multi-command SaveChanges in a transaction of
        // its own. SQLite has no nested transactions, so that inner BEGIN fails outright. The
        // transaction below is ours; EF is told to keep out of it, and told back afterwards
        // because the context outlives this call.
        var previousTransactionBehavior = context.Database.AutoTransactionBehavior;
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        // busy_timeout is not what bounds a contended write. Microsoft.Data.Sqlite retries
        // SQLITE_BUSY until the command timeout, thirty seconds by default, so the pragma's five
        // seconds became six waits rather than one. One budget, taken from the pragma, so a
        // caller stuck behind another writer gives up in seconds instead of pinning a request
        // thread for half a minute.
        var previousCommandTimeout = context.Database.GetCommandTimeout();
        context.Database.SetCommandTimeout(
            TimeSpan.FromMilliseconds(SqlitePragmas.BusyTimeoutMilliseconds));

        // Both are tracked so that failure undoes exactly what this call did and nothing more:
        // rolling back a transaction it never opened would discard the caller's uncommitted work,
        // and ChangeTracker.Clear would discard whatever the caller was tracking.
        var started = false;
        Booking? pending = null;
        OutboxMessage? announcement = null;
        try
        {
            context.Database.ExecuteSqlRaw("BEGIN IMMEDIATE;");
            started = true;

            // After the lock, not before it: BEGIN IMMEDIATE can wait out the whole busy-timeout
            // budget behind another writer, and a reading taken before that wait would compare a
            // hold's expiry against a clock several seconds stale. One reading, used by the hold
            // check and by the booking it writes, so both agree on when this call happened.
            var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

            // Two terms, summed, because a slot is unavailable for two different reasons and both
            // have to be inside the same BEGIN IMMEDIATE. Half-open on both sides, and MAX here is
            // SQLite's two-argument scalar maximum, so the rule matches SlotGenerator exactly: pad
            // by the larger of the two margins, never their sum, and let a slot start the moment
            // the padding ends.
            //
            // The second term is the proposal hold. Its expiry is compared against the clock here
            // rather than against a row the sweeper has marked, for the reason LocalBusySource
            // gives: a hold that has lapsed must stop holding whether or not anything has run.
            // The meeting behind it has to still be on for the same reason — a proposal left open
            // over a booking cancelled underneath it would hold a slot for a meeting that is off.
            var clashes = context.Database.SqlQuery<int>($"""
                SELECT
                  (SELECT COUNT(*)
                   FROM Bookings b
                   JOIN Links l ON l.Id = b.LinkId
                   LEFT JOIN Settings s ON s.Id = {Settings.SingletonId}
                   WHERE b.Status = {ConfirmedStatus}
                     AND b.StartUtc - (MAX(COALESCE(l.MarginMinutes, s.MarginMinutes, {NoMarginFallback}), {queryingMargin}) * {SecondsPerMinute}) < {endUtc}
                     AND b.EndUtc   + (MAX(COALESCE(l.MarginMinutes, s.MarginMinutes, {NoMarginFallback}), {queryingMargin}) * {SecondsPerMinute}) > {startUtc})
                  +
                  (SELECT COUNT(*)
                   FROM BookingProposals p
                   JOIN Bookings pb ON pb.Id = p.BookingId
                   JOIN Links pl ON pl.Id = pb.LinkId
                   LEFT JOIN Settings ps ON ps.Id = {Settings.SingletonId}
                   WHERE p.State = {OpenProposalState}
                     AND pb.Status = {ConfirmedStatus}
                     AND p.ExpiresAt > {now}
                     AND p.StartUtc - (MAX(COALESCE(pl.MarginMinutes, ps.MarginMinutes, {NoMarginFallback}), {queryingMargin}) * {SecondsPerMinute}) < {endUtc}
                     AND p.EndUtc   + (MAX(COALESCE(pl.MarginMinutes, ps.MarginMinutes, {NoMarginFallback}), {queryingMargin}) * {SecondsPerMinute}) > {startUtc})
                  AS Value
                """).AsEnumerable().Single();

            if (clashes > 0)
            {
                Abandon(started, pending, announcement);
                return new CreateBookingResult(false, Guid.Empty, null, SlotTakenReason);
            }

            var manageToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(ManageTokenBytes)).ToLowerInvariant();

            var bookingId = Guid.CreateVersion7();
            pending = new Booking
            {
                Id = bookingId,
                LinkId = linkId,
                StartUtc = startUtc,
                EndUtc = endUtc,
                Notes = notes,
                Status = BookingStatus.Confirmed,
                ManageTokenHash = Hash(manageToken),
                CreatedAt = now,
                Participants =
                [
                    new BookingParticipant
                    {
                        Id = Guid.CreateVersion7(),
                        BookingId = bookingId,
                        Role = ParticipantRole.Booker,
                        Name = booker.Name,
                        Email = booker.Email,
                        TimeZoneId = booker.TimeZoneId,
                    },
                ],
            };

            context.Bookings.Add(pending);

            // Inside the transaction this method already owns, and before the one SaveChanges
            // that carries the booking. That is the entire point of the outbox: the booking and
            // the announcement of it are the same INSERT batch inside the same BEGIN IMMEDIATE,
            // so there is no window in which one exists without the other — not a small window,
            // none. Enqueued here rather than by the endpoint because the endpoint could only do
            // it after the COMMIT, which is the dual write the pattern exists to delete.
            announcement = outbox.Enqueue(
                bookingId,
                OutboxMessageType.BookingCreated,
                BookingEventPayload.Created(bookingId, slug, start, duration).ToJson());

            context.SaveChanges();
            context.Database.ExecuteSqlRaw("COMMIT;");

            return new CreateBookingResult(true, pending.Id, manageToken, null);
        }
        catch (Exception exception) when (IsSlotAlreadyTaken(exception))
        {
            // The partial unique index fired — the backstop, not the mechanism.
            Abandon(started, pending, announcement);
            return new CreateBookingResult(false, Guid.Empty, null, SlotTakenReason);
        }
        catch
        {
            Abandon(started, pending, announcement);
            throw;
        }
        finally
        {
            context.Database.SetCommandTimeout(previousCommandTimeout);
            context.Database.AutoTransactionBehavior = previousTransactionBehavior;
        }
    }

    /// <summary>
    /// Hashes a manage token for storage and for lookup. Lower-cased first: the token is minted
    /// as lower-case hex, so a copy that came back upper-cased — retyped, or through a URL
    /// rewriter — would otherwise hash to something the unique index has never seen and the
    /// manage link would 404 with nothing to explain it.
    /// </summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.ToLowerInvariant())))
            .ToLowerInvariant();

    /// <summary>
    /// Undoes what this call did, and nothing else. Both guards matter: a bare ROLLBACK after a
    /// BEGIN IMMEDIATE that never ran would discard a transaction the caller opened — the shape a
    /// reschedule takes — and clearing the change tracker wholesale would discard entities the
    /// caller is holding. Left tracked, the three failed inserts would ride along on the caller's
    /// next SaveChanges and re-insert the booking this method just refused — and announce it.
    /// </summary>
    private void Abandon(bool started, Booking? pending, OutboxMessage? announcement)
    {
        if (started)
        {
            Rollback();
        }

        if (announcement is not null)
        {
            context.Entry(announcement).State = EntityState.Detached;
        }

        if (pending is null)
        {
            return;
        }

        // Copied first: detaching a participant makes EF fix up the navigation it is being read
        // from, and the failure path would then throw over the failure it is cleaning up after.
        foreach (var participant in pending.Participants.ToArray())
        {
            context.Entry(participant).State = EntityState.Detached;
        }

        context.Entry(pending).State = EntityState.Detached;
    }

    /// <summary>
    /// A failed statement inside a transaction leaves that transaction open, so this normally
    /// succeeds and is what undoes the insert. It is guarded because the connection can still be
    /// out of one — a lock this call never won, say — and "cannot rollback - no transaction is
    /// active" would replace the exception worth reading with one raised while cleaning up.
    /// </summary>
    private void Rollback()
    {
        try
        {
            context.Database.ExecuteSqlRaw("ROLLBACK;");
        }
        catch (SqliteException)
        {
            // No transaction left to roll back. Nothing to undo and nothing to report.
        }
    }

    /// <summary>
    /// EF wraps a constraint violation raised by SaveChanges in DbUpdateException, so the check
    /// has to look through the exception it is handed rather than at its type.
    /// </summary>
    private static bool IsSlotAlreadyTaken(Exception exception)
    {
        var sqlite = (exception as SqliteException) ?? (exception.InnerException as SqliteException);
        return sqlite?.SqliteExtendedErrorCode == SqliteConstraintUniqueErrorCode;
    }
}
