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
                logger.LogWarning(exception, "A proposal sweep failed. The next tick will try again.");
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
        var lapsed = await context.BookingProposals
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Link)
            .Include(proposal => proposal.Booking)
            .ThenInclude(booking => booking.Participants)
            .Where(proposal =>
                (proposal.NotifiedAt == null)
                && (((proposal.State == ProposalState.Open) && (proposal.ExpiresAt <= now))
                    || (proposal.State == ProposalState.Expired)))
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

        foreach (var proposal in lapsed)
        {
            proposal.State = ProposalState.Expired;

            // When it actually lapsed, not when this noticed. The sweeper is allowed to be late
            // and the row must not record the lateness as the decision.
            proposal.DecidedAt ??= proposal.ExpiresAt;
            proposal.NotifiedAt = now;

            // Saved before the send, so a relay that hangs cannot make the next tick send again.
            // The trade is stated plainly: a crash between here and the send loses one message,
            // which is a message about a meeting that has not changed.
            await context.SaveChangesAsync(cancellationToken);

            await mailer.SendAsync(
                proposal.Booking, proposal, OperatorNotice.Expired, timeZoneId, cancellationToken);
        }
    }
}
