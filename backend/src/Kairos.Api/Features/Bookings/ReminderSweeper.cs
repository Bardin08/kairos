using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;
using Kairos.Api.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>Sends the reminders that have come due, and gives up on the ones that never will.</summary>
/// <remarks>
/// Shaped after <see cref="ProposalSweeper"/> — a tick that swallows its own failure, a scope per
/// sweep, a batch sized on the relay's timeout, and one save per row — and it differs in what a
/// late tick means. There, late is late mail about a hold that stopped holding on its own. Here,
/// late is still useful: the state is <see cref="Reminder"/>, written in the booking's own
/// transaction, so a process that was down over a due moment sends on the tick after it comes
/// back. That is the whole of what makes this durable rather than scheduled, and it is why nothing
/// here reasons about how long ago the due moment was — only about whether the meeting has begun.
/// </remarks>
public sealed class ReminderSweeper(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<EmailOptions> email,
    ILogger<ReminderSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    /// <summary>Enough of a failure to act on, in a database meant to stay small.</summary>
    private const int MaxErrorLength = 500;

    /// <summary>
    /// How many this tick will attempt. Derived, not chosen: a send to a relay that never answers
    /// costs the whole of <see cref="EmailSender.SendTimeout"/> and they are made one at a time,
    /// so this is the largest batch whose worst case still fits inside the interval that schedules
    /// it. A dead relay then costs one tick's work per tick whatever the backlog is, and taking
    /// the batch oldest-due-first means the cap delays rather than starves.
    /// </summary>
    public static readonly int MaxRemindersPerTick = (int)(TickInterval / EmailSender.SendTimeout);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A tick that threw must not take the service down: the next one is a minute away
                // and every reminder it failed to send is still due on it.
                logger.LogError(exception, "A reminder sweep failed. The next tick will try again.");
            }
        }
    }

    /// <summary>
    /// One pass. Public so a test can drive it without a timer: the loop above is the schedule.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        // A scope per sweep, and never a context shared with a previous one: DbContext is scoped,
        // is not thread-safe, and this ticks independently of every request and of the outbox
        // services beside it.
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        var mailer = scope.ServiceProvider.GetRequiredService<BookingMailer>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();

        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

        await ForgetSpentSecretsAsync(context, now, cancellationToken);

        // Before the batch and after the scrub. Whether a relay exists is a fact about the process
        // rather than about any row, so it is not written onto rows: nothing is attempted, nothing
        // is stamped, no secret is spent, and every due reminder goes out unchanged on the tick
        // after the configuration is fixed. That recoverability is the whole point of returning
        // here rather than finishing the rows. The case is not the relayless install where nobody
        // is worse off — it is one deploy with a missing Email__Host, a secret that failed to
        // mount or a typo in a values file, which IOptions holds for the life of the process.
        // Stamping SentAt on that path would mark every reminder due in the window delivered,
        // destroy the token each one needed, and leave nothing in the database saying it never
        // went — the exact outcome this feature exists to prevent.
        //
        // The scrub still runs above: a row whose meeting has started is one nobody will ever send
        // whatever the relay is doing, so its ciphertext is dead either way.
        //
        // Debug, and once per sweep rather than once per row, because Program.cs already logs this
        // at Warning once at startup with the settings to fix it. A line a minute per row about a
        // relay that does not exist is the noise this return exists to avoid; climbing Attempts
        // for ever on rows nothing ever attempted is the record it exists to keep honest.
        if (!email.Value.IsConfigured)
        {
            logger.LogDebug(
                "{Section} is not configured; reminders are held until it is.",
                EmailOptions.SectionName);
            return;
        }

        // Tracked, which is the one place rule 8's AsNoTracking() does not apply and for
        // ProposalSweeper's reason: this read decides about rows it then writes back through, and
        // the change tracker is what keeps that write to the columns this actually touched.
        //
        // The two included chains are the message's own needs rather than the predicate's — the
        // link for its name, the booker for the address and the zone the time is rendered in.
        // Loaded here rather than per row, so the batch costs one round trip instead of one plus
        // twice its size.
        var due = await context.Reminders
            .Where(reminder =>
                (reminder.SentAt == null)
                && (reminder.DueAt <= now)
                && (reminder.Booking.Status == BookingStatus.Confirmed)
                && (reminder.Booking.StartUtc > now))
            .OrderBy(reminder => reminder.DueAt)
            .Take(MaxRemindersPerTick)
            .Include(reminder => reminder.Booking).ThenInclude(booking => booking.Link)
            .Include(reminder => reminder.Booking).ThenInclude(booking => booking.Participants)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            // Which is every sweep on an ordinary day, and the reason the join URLs are read below
            // this rather than alongside the batch.
            return;
        }

        // The join URL is this message's action, so it is read for the whole batch in one query
        // rather than per row. A third Include would have said it in one, and cannot: Booking has
        // no navigation to its conference links, and giving it one so a mail could use it would be
        // a schema change made by a message — FollowUpWorker.JoinUrlAsync settled that already.
        //
        // A lookup rather than a dictionary because the unique index is over (BookingId, Provider):
        // two providers could each have placed a conference, and the notice offers one link.
        // Ordered by provider so that which one is a fact rather than whatever the database
        // happened to return — a retry after a refusal then offers the same link as the attempt
        // the invitee never saw, instead of two reminders disagreeing about where to join.
        var bookingIds = due.Select(reminder => reminder.BookingId).ToList();
        var joins = (await context.ConferenceLinks
                .AsNoTracking()
                .Where(row => bookingIds.Contains(row.BookingId))
                .OrderBy(row => row.Provider)
                .Select(row => new { row.BookingId, row.JoinUrl })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.BookingId, row => row.JoinUrl);

        foreach (var reminder in due)
        {
            await SendAsync(
                context,
                mailer,
                protector,
                reminder,
                joins[reminder.BookingId].FirstOrDefault(),
                now,
                cancellationToken);
        }
    }

    /// <summary>
    /// One reminder, and one save. Not one save per sweep: batched, the stamps would all be
    /// written after their messages had gone, so a single failed save loses every marker in the
    /// batch and the next tick sends the lot again.
    /// </summary>
    /// <remarks>
    /// <see cref="Reminder.SentAt"/> is stamped on the relay's answer rather than on the attempt,
    /// which <see cref="BookingProposal.NotifiedAt"/> already reasons out: a tick that dies between
    /// the send and the stamp and a relay that refuses leave the same row, the next tick cannot
    /// tell them apart, and does not need to — the answer to both is to send again. A duplicate
    /// reminder is an annoyance; a missing one is the feature not working.
    ///
    /// <see cref="Reminder.Attempts"/> and <see cref="Reminder.LastError"/> are for a human and for
    /// nothing else. Nothing branches on them — what removes a row from the queue is its meeting
    /// starting, not a cap, because a reminder is worth retrying right up until the moment it stops
    /// being true. What they buy is the answer to "why was I never reminded", which a single
    /// nullable timestamp could not give, and which is what the columns were justified by.
    /// </remarks>
    private async Task SendAsync(
        KairosDbContext context,
        BookingMailer mailer,
        SecretProtector protector,
        Reminder reminder,
        string? joinUrl,
        long now,
        CancellationToken cancellationToken)
    {
        reminder.Attempts++;

        try
        {
            // Ciphertext on the row, plaintext in the link. Null is ordinary rather than a fault:
            // a booking written before the column existed, or one moved from inside the lead to
            // outside it, has nothing to carry — and the message is composed to read without it.
            var manageToken = reminder.Secret is null ? null : protector.Unprotect(reminder.Secret);

            if (!await mailer.SendReminderAsync(reminder.Booking, manageToken, joinUrl, cancellationToken))
            {
                // BookingMailer swallows a refusal to keep rule 14 — see FollowUpWorker's
                // RequireSentAsync, which asks the same question back for the same reason. A caller
                // that retries has to know, and the row is where the answer is kept.
                throw new InvalidOperationException(
                    $"The relay would not take the reminder for booking {reminder.BookingId}.");
            }

            Finish(reminder, now);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            reminder.LastError = Truncate(exception.Message);

            logger.LogWarning(
                exception,
                "Reminder {Reminder} for booking {Booking} failed (attempt {Attempts}). The next "
                + "tick will try again until the meeting starts.",
                reminder.Id,
                reminder.BookingId,
                reminder.Attempts);
        }

        // Uncancellable, for FollowUpWorker's reason: this records what a relay has already done
        // or refused, and a shutdown between the send and the stamp is what causes a repeat.
        await context.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// The row is done with. The stamp is the record; the secret goes with it because nothing will
    /// ever spend it again, and a sent reminder that kept its ciphertext would hold a live manage
    /// token for the life of the booking — which is the thing rule 15 exists to stop.
    /// </summary>
    private static void Finish(Reminder reminder, long now)
    {
        reminder.SentAt = now;
        reminder.LastError = null;
        reminder.Secret = null;
    }

    /// <summary>
    /// Drops the ciphertext on every row the sweep above will never pick again. A reminder whose
    /// meeting has begun, or whose booking left Confirmed, is one nobody will ever send, and
    /// <see cref="FollowUpWorker"/>'s rule applies to it unchanged: keep the row, so an operator
    /// can still find out that nobody was reminded, and drop the secret, because a value that must
    /// not sit in the database in the clear should not sit there encrypted once nothing will print
    /// it. Without this a relay down over one lead strands a live manage token for good.
    /// </summary>
    /// <remarks>
    /// An UPDATE rather than a read and a loop, and it runs before the tracked read above so the
    /// two cannot disagree about a row: ExecuteUpdate never loads anything and the change tracker
    /// would not hear about it. It leads on Secret, which the (SentAt, DueAt) index does not cover,
    /// so it scans — affordable because Reminders holds about one row per booking still ahead, and
    /// the thing to reach for if that ever stops being true is an index, not a schedule.
    /// </remarks>
    private static Task ForgetSpentSecretsAsync(
        KairosDbContext context, long now, CancellationToken cancellationToken) =>
        context.Reminders
            .Where(reminder =>
                (reminder.Secret != null)
                && ((reminder.SentAt != null)
                    || (reminder.Booking.Status != BookingStatus.Confirmed)
                    || (reminder.Booking.StartUtc <= now)))
            .ExecuteUpdateAsync(
                update => update.SetProperty(reminder => reminder.Secret, (string?)null),
                cancellationToken);

    private static string Truncate(string message) =>
        message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
