using System.Text;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

using MimeKit;
using MimeKit.Text;

using NodaTime;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// What the organizer is told about their own suggestions. Plain text, no calendar part, no token.
/// </summary>
/// <remarks>
/// A second class rather than a branch in <see cref="BookingMailer"/>, because nothing about the
/// two messages is the same: different recipient, no manage URL, no .ics, and a link to the
/// workbench rather than a token page. Three orthogonal conditionals through one composer is how
/// the MIME shape Part B spent a week getting right gets broken by somebody editing a sentence.
///
/// The recipient is <see cref="EmailOptions.From"/> — the organizer's own mailbox and the only
/// operator address Kairos has. A second configuration key would be a deployment surface for a
/// feature nobody has asked for.
/// </remarks>
public sealed class OperatorMailer(
    EmailSender sender,
    IOptions<EmailOptions> options,
    ILogger<OperatorMailer> logger)
{
    /// <summary>
    /// The route the frontend claims: <c>/admin/meetings</c>, the workbench screen this notice
    /// exists to send the operator back to. Behind the session gate, unlike the two paths
    /// <see cref="BookingMailer"/> builds, which are keyed by a token instead.
    /// </summary>
    private const string MeetingsPath = "admin/meetings";

    /// <summary>
    /// Sends, or does not, and either way returns — rule 14 at the only place it can be enforced.
    /// The proposal has already been closed before this runs, and nothing here can reach back and
    /// reopen it.
    /// </summary>
    /// <returns>
    /// Whether the caller has anything left to do about this notice. True when the relay took it
    /// AND when there is no relay to take it — the same answer from where the caller stands.
    /// False only for a message that was meant to go and did not.
    ///
    /// Reporting it does not weaken rule 14, which is about a provider failure never failing a
    /// BOOKING, and nothing upstream of here is a booking — the same carve-out
    /// <see cref="BookingMailer.SendProposalAsync"/> takes. The sweeper writes its idempotency
    /// marker on a true and leaves the row for the next tick on a false, which is all that stands
    /// between a relay having a bad minute and every lapse in it recorded as announced for good.
    /// </returns>
    public async Task<bool> SendAsync(
        Booking booking,
        BookingProposal proposal,
        OperatorNotice notice,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogDebug(
                "{Section} is not configured; proposal {Proposal} closed without a {Notice} being sent.",
                EmailOptions.SectionName,
                proposal.Id,
                notice);
            return true;
        }

        try
        {
            var message = Compose(booking, proposal, notice, timeZoneId);
            var response = await sender.SendAsync(message, cancellationToken);
            logger.LogInformation(
                "{Notice} for proposal {Proposal} accepted by the relay as {MessageId}: {Response}",
                notice,
                proposal.Id,
                message.MessageId,
                response);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not tell the operator about proposal {Proposal}. The proposal is closed and "
                + "the meeting is unchanged.",
                proposal.Id);
            return false;
        }
    }

    /// <param name="timeZoneId">
    /// The organizer's own zone, because the organizer is who this is addressed to — the same rule
    /// <see cref="BookingMailer"/> follows in the other direction. Passed in rather than read here
    /// because this is a singleton with no database of its own.
    /// </param>
    public MimeMessage Compose(
        Booking booking,
        BookingProposal proposal,
        OperatorNotice notice,
        string timeZoneId)
    {
        var settings = options.Value;
        var organizer = MailboxAddress.Parse(settings.From!);
        var booker = booking.Participants.First(participant => participant.Role == ParticipantRole.Booker);
        var zone = DateTimeZoneProviders.Tzdb[timeZoneId];
        var proposed = Instant.FromUnixTimeSeconds(proposal.StartUtc).InZone(zone);
        var current = Instant.FromUnixTimeSeconds(booking.StartUtc).InZone(zone);

        var headline = notice == OperatorNotice.Declined
            ? $"{booker.Name} declined the new time for {booking.Link.Name}."
            : $"{booker.Name} did not answer the new time for {booking.Link.Name}.";

        // The reassurance, or the correction. A cancel closes an OPEN hold and nothing else, so a
        // row already Expired can reach the operator about a meeting they themselves cancelled —
        // where telling them it stays put would be the one sentence here that is flatly untrue.
        var standing = booking.Status == BookingStatus.Confirmed
            ? $"The meeting stays where it is: {When(current)}."
            : $"The meeting has since been cancelled; it was {When(current)}.";

        var body = new TextPart(TextFormat.Plain);
        body.SetText(Encoding.UTF8, $"""
            {headline}

            You suggested {When(proposed)}.

            {standing}

            {OutgoingMail.Url(settings.PublicBaseUrl!, MeetingsPath)}
            """);

        return OutgoingMail.Envelope(headline, body, organizer, organizer);
    }

    /// <summary>
    /// A day and an hour in one phrase, because every instant in this message is read against
    /// another one and a column of half-written times is how a call gets missed.
    /// </summary>
    private static string When(ZonedDateTime value) =>
        $"{OutgoingMail.Date(value)} at {OutgoingMail.Time(value)} ({value.Zone.Id})";
}
