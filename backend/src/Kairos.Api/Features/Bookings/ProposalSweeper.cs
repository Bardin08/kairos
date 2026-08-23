using Kairos.Api.Integrations.Email;
using Kairos.Api.Persistence;
using Kairos.Api.Persistence.Entities;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Bookings;

/// <summary>Turns lapsed proposals into closed ones and tells the operator.</summary>
/// <remarks>
/// It is not what makes the expiry correct, and that is the design: every conflict reader compares
/// <see cref="BookingProposal.ExpiresAt"/> against the clock itself, so a hold stops holding the
/// instant it lapses whether or not this has run, and a sweeper that is late or dead makes mail
/// late rather than availability wrong. What it owns is what a WHERE clause cannot do: noticing.
///
/// Nor does re-proposing wait on it, though for one pull request it did. The filtered unique index
/// over BookingId WHERE State = Open is a row rather than a predicate, so a lapsed hold left Open
/// occupies the single slot the next suggestion needs;
/// <see cref="ProposalWriter.ExpireLapsedAsync"/> closes it on the propose route and leaves
/// <see cref="BookingProposal.NotifiedAt"/> null, which is why the query below asks about that
/// column rather than the state alone.
///
/// Registered beside <see cref="Integrations.Webhooks.MeetingEndedPoller"/> and ticking on its
/// minute, without that service's sweep-at-startup: a tick missed there is a meeting.ended nobody
/// queued, and a tick missed here is a late mail about a meeting that has not moved.
/// </remarks>
public sealed class ProposalSweeper(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<ProposalSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    /// <summary>How many notices one tick will attempt.</summary>
    /// <remarks>
    /// Derived rather than picked: a send to a relay that never answers costs the whole of
    /// <see cref="EmailSender.SendTimeout"/> and they go one at a time, so this is the largest batch
    /// whose worst case still fits the interval that schedules it. That buys the one bound this
    /// service needs — a dead relay costs one tick's work per tick, whatever the backlog, where an
    /// uncapped sweep would retry every unannounced lapse ever recorded, every minute, each attempt
    /// blocking for fifteen seconds.
    ///
    /// The cap delays and does not starve: the batch is oldest lapse first, so a backlog drains at
    /// this rate per minute once the relay answers again.
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
    /// One pass. Public so a test can drive it without a timer: the loop above is the schedule.
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
        // that crashed between the save and the send leaves exactly that, which is why NotifiedAt
        // exists rather than the state alone deciding. An answered proposal is never on this list:
        // nobody needs telling that nobody replied to a suggestion that was declined.
        // Tracked, the one read in this feature that is: every other proposal read is AsNoTracking
        // under rule 8 because it writes through a conditional UPDATE naming its own predicate.
        // This one closes rows it has already decided about, twice each, and the change tracker is
        // what lets the second write touch the marker alone.
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

        // The organizer's own zone, since these messages are addressed to the organizer. Read once
        // per sweep rather than once per proposal: one row, and a sweep is a single moment.
        var timeZoneId = await context.Settings
            .Where(row => row.Id == Settings.SingletonId)
            .Select(row => row.TimeZoneId)
            .SingleAsync(cancellationToken);

        // Two saves per row rather than two per sweep, and it is the MARKER save that makes the
        // difference: batched, its rows have all been sent already, so one failed save loses every
        // marker at once and the next tick mails the lot again. Per row, a marker lands with the
        // message it belongs to and a failure costs the row it happened on.
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
                // The relay would not take it. The row keeps a null marker, the honest record, and
                // the next tick finds it on the query's second arm. Writing the marker anyway is the
                // failure this ordering exists to prevent, and the likelier one by far: a process
                // dying between two adjacent saves is rare, a relay having a bad minute is a Tuesday.
                continue;
            }

            // The marker last, and only now. A process dying in the gap above leaves the row Expired
            // and unmarked, which the second arm picks up the same way, so both failures land on the
            // same side. At-least-once on purpose: a duplicate "nobody answered your suggestion" is
            // an annoyance, a lost one is an operator who never learns the suggestion died.
            proposal.NotifiedAt = now;
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
