using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Turns lapsed proposals into closed ones and tells the operator.
/// </summary>
/// <remarks>
/// It is not what makes the expiry correct, and that is the design. Every conflict reader compares
/// <see cref="BookingProposal.ExpiresAt"/> against the clock itself, so a hold stops holding at the
/// instant it lapses whether or not this has run — a sweeper that is late, dead, or never started
/// makes mail late and never makes availability wrong. What it owns is the part a WHERE clause
/// cannot do: noticing, so somebody can be told.
///
/// Nor does re-proposing wait on it, though for one pull request it did. The filtered unique index
/// over BookingId WHERE State = Open is a row rather than a predicate, so it is the one reader a
/// clock cannot be folded into: a lapsed hold left Open goes on occupying the single slot the next
/// suggestion needs. <see cref="ProposalWriter.ExpireLapsedAsync"/> closes it on the propose route
/// itself, and deliberately leaves <see cref="BookingProposal.NotifiedAt"/> null — which hands the
/// notice to this service rather than swallowing it, and is why the query below asks about that
/// column rather than about the state alone. So nothing waits on this tick: a row it has not
/// reached yet costs a late mail and nothing else.
///
/// Registered beside <see cref="Integrations.Webhooks.MeetingEndedPoller"/> and ticking on its
/// minute. It does not copy that service's sweep-at-startup and does not need to: a tick missed
/// there is a meeting.ended nobody queued, and a tick missed here is a late mail about a meeting
/// that has not moved.
/// </remarks>
public sealed class ProposalSweeper(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<ProposalSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many notices one tick will attempt.
    /// </summary>
    /// <remarks>
    /// Derived rather than picked. A send to a relay that never answers costs the whole of
    /// <see cref="EmailSender.SendTimeout"/> and they are made one at a time, so this is the
    /// largest batch whose worst case still fits inside the interval that schedules it. What it
    /// buys is the one bound this service needs: a dead relay costs one tick's work per tick,
    /// whatever the backlog is and however old the deployment is — where an uncapped sweep would
    /// retry every unannounced lapse ever recorded, every minute, each attempt blocking for
    /// fifteen seconds.
    ///
    /// The cap delays and does not starve, because the batch is taken oldest lapse first: a
    /// backlog drains at this rate per minute as soon as the relay answers again.
    /// </remarks>
    public static readonly int MaxNoticesPerTick = (int)(TickInterval / EmailSender.SendTimeout);

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
                // and the holds it failed to close have already stopped holding anything.
                logger.LogError(exception, "A proposal sweep failed. The next tick will try again.");
            }
        }
    }

    /// <summary>
    /// One pass. Public so a test can drive it without a timer — the loop above is the schedule,
    /// this is the work.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        // A scope per sweep, and never a context shared with a previous one: DbContext is scoped,
        // is not thread-safe, and this ticks independently of every request and of the outbox
        // services beside it.
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<KairosDbContext>();
        var mailer = scope.ServiceProvider.GetRequiredService<OperatorMailer>();

        var now = clock.GetCurrentInstant().ToUnixTimeSeconds();

        // Open and lapsed, plus anything already Expired that nobody has been told about — a tick
        // that crashed between the save and the send left exactly that, and it is the reason
        // NotifiedAt exists rather than the state alone deciding. An answered proposal is not on
        // this list however long ago its expiry ran out: nobody needs telling that nobody replied
        // to a suggestion that was declined.
        // Tracked, and the one read in this feature that is — every other proposal read is
        // AsNoTracking under rule 8, because every other one writes through a conditional UPDATE
        // that names its own predicate. This one has no predicate to write: it closes rows it has
        // already decided about, twice each, and a change tracker is what lets the second write
        // touch the marker alone instead of restating the state beside it.
        var lapsed = await context.BookingProposals
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Link)
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Participants)
            .Where(proposal =>
                (proposal.NotifiedAt == null)
                && (((proposal.State == ProposalState.Open) && (proposal.ExpiresAt <= now))
                    || (proposal.State == ProposalState.Expired)))
            .OrderBy(proposal => proposal.ExpiresAt)
            .Take(MaxNoticesPerTick)
            .ToListAsync(cancellationToken);

        if (lapsed.Count == 0)
        {
            // Which is every sweep on an ordinary day, and the reason the settings read below is
            // behind this rather than at the top of the method.
            return;
        }

        // The organizer's own zone, because the organizer is who these messages are addressed to.
        // Read once per sweep rather than once per proposal: it is one row that changes when the
        // operator changes it, and a sweep is a single moment.
        var timeZoneId = await context.Settings
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);

        // Two saves per row rather than two per sweep, and it is the MARKER save that makes the
        // difference. Batch that one and its rows have all had their messages sent already, so a
        // single failed save loses every marker in the batch at once and the next tick mails the
        // lot a second time. Per row, a marker lands with the message it belongs to and a failure
        // costs the row it happened on.
        foreach (var proposal in lapsed)
        {
            proposal.State = ProposalState.Expired;

            // When it actually lapsed, not when this noticed. The sweeper is allowed to be late
            // and the row must not record the lateness as the decision.
            proposal.DecidedAt ??= proposal.ExpiresAt;
            await context.SaveChangesAsync(cancellationToken);

            var announced = await mailer.SendAsync(
                proposal.Booking, proposal, OperatorNotice.Expired, timeZoneId, cancellationToken);

            if (!announced)
            {
                // The relay would not take it. The row keeps a null marker, which is the honest
                // record — nobody has been told — and the next tick finds it on the query's second
                // arm and tries again. Writing the marker here anyway is the failure this whole
                // ordering exists to prevent, and it is the likelier one by far: a process dying
                // between two adjacent saves is rare, and a relay having a bad minute is a Tuesday.
                continue;
            }

            // The marker last, and only now. A process that dies in the gap above leaves the row
            // Expired and unmarked, which the second arm picks up in exactly the same way — so
            // both failures land on the same side. This is at-least-once on purpose: a duplicate
            // "nobody answered your suggestion" is an annoyance, and a lost one is an operator who
            // never learns their suggestion died over a meeting they still believe is being
            // decided.
            proposal.NotifiedAt = now;
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
