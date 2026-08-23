using System.Text;

using Kairos.Api.Persistence.Entities;

using Microsoft.Extensions.Options;

using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;

using NodaTime;
using NodaTime.Text;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// What the organizer is told about their own suggestions. Plain text, no calendar part, no token.
/// </summary>
/// <remarks>
/// A second class rather than a branch in <see cref="BookingMailer"/>, and the reason is that
/// nothing about the two messages is the same: different recipient, no manage URL, no .ics, and a
/// link to the workbench rather than to a token page. Three orthogonal conditionals through one
/// composer is how the MIME shape that Part B spent a week getting right gets broken by somebody
/// editing a sentence.
///
/// The recipient is <see cref="EmailOptions.From"/> — the organizer's own mailbox and the only
/// operator address Kairos has. A second configuration key for it would be a deployment surface
/// for a feature nobody has asked for.
/// </remarks>
public sealed class OperatorMailer(
    EmailSender sender,
    IOptions<EmailOptions> options,
    ILogger<OperatorMailer> logger)
{
    private const string MeetingsPath = "admin/meetings";

    private static readonly ZonedDateTimePattern DatePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("dddd d MMMM yyyy", DateTimeZoneProviders.Tzdb);

    private static readonly ZonedDateTimePattern TimePattern =
        ZonedDateTimePattern.CreateWithInvariantCulture("HH:mm", DateTimeZoneProviders.Tzdb);

    /// <summary>
    /// Sends, or does not, and either way returns — rule 14 at the only place it can be enforced.
    /// The proposal has already been closed before this runs, and nothing here can reach back and
    /// reopen it.
    /// </summary>
    public async Task SendAsync(
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
            return;
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
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Could not tell the operator about proposal {Proposal}. The proposal is closed and "
                + "the meeting is unchanged; there is no retry.",
                proposal.Id);
        }
    }

    /// <summary>
    /// The message, exactly as it goes on the wire.
    /// </summary>
    /// <param name="timeZoneId">
    /// The organizer's own zone, because the organizer is who this is addressed to — the same rule
    /// <see cref="BookingMailer"/> follows in the other direction, where the booker's zone is used
    /// for the booker's mail. It is passed in rather than read here because this is a singleton
    /// with no database of its own, and every caller has already read the settings row.
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

        var body = new TextPart(TextFormat.Plain);
        body.SetText(Encoding.UTF8, $"""
            {headline}

            You suggested {DatePattern.Format(proposed)} at {TimePattern.Format(proposed)} ({zone.Id}).

            The meeting stays where it is: {DatePattern.Format(current)} at {TimePattern.Format(current)} ({zone.Id}).

            {settings.PublicBaseUrl!.TrimEnd('/')}/{MeetingsPath}
            """);

        var message = new MimeMessage
        {
            Subject = headline,
            Body = body,
        };

        message.From.Add(organizer);
        message.To.Add(organizer);

        // The sending domain rather than MimeKit's default, which is the machine's hostname — a
        // random hex string in a container, and a forgery signal to a filter. The reasoning is
        // BookingMailer.Envelope's; it is not shared with it because nothing else in these two
        // messages is.
        message.MessageId = MimeUtils.GenerateMessageId(organizer.Domain);

        return message;
    }
}
