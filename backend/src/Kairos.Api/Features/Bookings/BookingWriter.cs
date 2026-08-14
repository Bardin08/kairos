using System.Security.Cryptography;
using System.Text;

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
public sealed class BookingWriter(KairosDbContext context, IClock clock)
{
    /// <summary>The slot conflicts with a confirmed booking. The only failure this returns.</summary>
    public const string SlotTakenReason = "slot_taken";

    private const int ManageTokenBytes = 32;

    private const int SecondsPerMinute = 60;

    /// <summary>SQLITE_CONSTRAINT — a unique index fired, here the confirmed-start backstop.</summary>
    private const int SqliteConstraintErrorCode = 19;

    private static readonly int ConfirmedStatus = (int)BookingStatus.Confirmed;

    /// <summary>
    /// EF's BeginTransaction issues a DEFERRED transaction, which takes a write lock only when it
    /// first writes. Two deferred transactions that both read then write deadlock in a way
    /// busy_timeout cannot rescue, because SQLite returns SQLITE_BUSY immediately when waiting
    /// could never resolve it. BEGIN IMMEDIATE takes the write lock up front, which is what makes
    /// check-then-insert correct here. This is the only raw SQL in the codebase.
    /// </summary>
    public CreateBookingResult TryCreate(
        Guid linkId,
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
        try
        {
            context.Database.ExecuteSqlRaw("BEGIN IMMEDIATE;");

            // Half-open on both sides, and MAX here is SQLite's two-argument scalar maximum, so
            // the rule matches SlotGenerator exactly: pad by the larger of the two margins,
            // never their sum, and let a slot start the moment the padding ends.
            var clashes = context.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM Bookings b
                JOIN Links l ON l.Id = b.LinkId
                CROSS JOIN Settings s
                WHERE s.Id = {Settings.SingletonId}
                  AND b.Status = {ConfirmedStatus}
                  AND b.StartUtc - (MAX(COALESCE(l.MarginMinutes, s.MarginMinutes), {queryingMargin}) * {SecondsPerMinute}) < {endUtc}
                  AND b.EndUtc   + (MAX(COALESCE(l.MarginMinutes, s.MarginMinutes), {queryingMargin}) * {SecondsPerMinute}) > {startUtc}
                """).AsEnumerable().Single();

            if (clashes > 0)
            {
                Rollback();
                return new CreateBookingResult(false, Guid.Empty, null, SlotTakenReason);
            }

            var manageToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(ManageTokenBytes)).ToLowerInvariant();

            var bookingId = Guid.CreateVersion7();
            var booking = new Booking
            {
                Id = bookingId,
                LinkId = linkId,
                StartUtc = startUtc,
                EndUtc = endUtc,
                Notes = notes,
                Status = BookingStatus.Confirmed,
                ManageTokenHash = Hash(manageToken),
                CreatedAt = clock.GetCurrentInstant().ToUnixTimeSeconds(),
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

            context.Bookings.Add(booking);
            context.SaveChanges();
            context.Database.ExecuteSqlRaw("COMMIT;");

            return new CreateBookingResult(true, booking.Id, manageToken, null);
        }
        catch (Exception exception) when (IsConstraintViolation(exception))
        {
            // The partial unique index fired — the backstop, not the mechanism.
            Rollback();
            return new CreateBookingResult(false, Guid.Empty, null, SlotTakenReason);
        }
        catch
        {
            Rollback();
            throw;
        }
        finally
        {
            context.Database.AutoTransactionBehavior = previousTransactionBehavior;
        }
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// SQLite rolls a failed statement back on its own, and then ROLLBACK throws "cannot rollback
    /// - no transaction is active". Rethrowing that would replace the exception worth reading
    /// with the one raised while cleaning up after it, so the cleanup stays silent.
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
    private static bool IsConstraintViolation(Exception exception)
    {
        var sqlite = (exception as SqliteException) ?? (exception.InnerException as SqliteException);
        return sqlite?.SqliteErrorCode == SqliteConstraintErrorCode;
    }
}
