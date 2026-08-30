using Kairos.Api.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Kairos.Api.Integrations.Email;

/// <summary>
/// The one home for the rule that decides what a notice quotes.
/// </summary>
/// <remarks>
/// Read back from the row rather than carried out of the call that placed the event, for the
/// reason the join URL is: the calendar entry commits inside the sync, and a caller holding a
/// local would be describing a write it never saw finish.
///
/// Both halves fall back together, never one of them. A provider's UID under the mail From is a
/// meeting the client already holds, re-announced by an address it has never seen own it — the
/// one combination this design cannot predict, so it is not one this code can produce.
///
/// The credential is read for its label alone, which is a column and not the protected payload,
/// so this goes to the table rather than through <see cref="IntegrationCredentialStore"/>: that
/// class is the one door to the secret, and nothing here opens one.
///
/// Nothing about mail is read here, deliberately. This runs on the booking's own request whether
/// or not a relay is configured, and a resolver that parsed the sending address would fail the
/// booking of a deployment that has none — stricter than the mailer it feeds, which answers that
/// question for itself and sends nothing.
/// </remarks>
public sealed class MeetingIdentityResolver(KairosDbContext context)
{
    public async Task<MeetingIdentity> ResolveAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var fallback = new MeetingIdentity(bookingId.ToString(), null);

        var entry = await context.CalendarEntries.AsNoTracking()
            .Where(row => row.BookingId == bookingId)
            .Select(row => new { row.Provider, row.CalendarUid })
            .FirstOrDefaultAsync(cancellationToken);

        if (entry?.CalendarUid is not { Length: > 0 } uid)
        {
            return fallback;
        }

        var account = await context.IntegrationCredentials.AsNoTracking()
            .Where(row => row.Provider == entry.Provider)
            .Select(row => row.AccountLabel)
            .FirstOrDefaultAsync(cancellationToken);

        return account is { Length: > 0 } owner ? new MeetingIdentity(uid, owner) : fallback;
    }
}
