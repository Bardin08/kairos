using Kairos.Api.Persistence;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// Reading the operator's invites, every link at once. Separate from <see cref="InviteWriter"/>
/// because this is the query side of the same rows, and because the fold it applies cannot run
/// where the query does: <see cref="InviteWriter.StateAt"/> needs a clock, which SQLite has no
/// access to.
/// </summary>
public sealed class InviteReader(KairosDbContext context, IClock clock)
{
    /// <summary>
    /// Every invite on every link, newest first, each row naming the link it sits on. Two rows
    /// sharing a created-at second are ordered by their ids, so a regenerated invite always reads
    /// directly above the row it replaced rather than either side of it.
    /// </summary>
    public async Task<InviteView[]> AllAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();

        // AsNoTracking, then folded once the rows are in hand. The state is not a column, so no
        // query could have filtered or sorted on it; a list already holding its rows has no reason
        // to ask the database a question C# can answer about them.
        //
        // CreatedAt is unix SECONDS, so a regenerate — which revokes one invite and adds its
        // replacement — reliably produces a tie, and the operator would see the two rows in
        // whatever order the database chose. The id breaks it: keys here are UUIDv7 stored as
        // canonical hyphenated TEXT, which sorts lexicographically and therefore chronologically.
        // Persistence.KeyGenerationTests pins both halves of that — the storage format and the
        // sort end to end on Bookings, and every Guid key minting a v7 at the model level.
        //
        // The link's name comes off the navigation inside the Select rather than through an
        // Include, so the join carries one column instead of the whole Link row.
        var rows = await context.PersonalInvites.AsNoTracking()
            .OrderByDescending(invite => invite.CreatedAt)
            .ThenByDescending(invite => invite.Id)
            .Select(invite => new { Invite = invite, LinkName = invite.Link.Name })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new InviteView(
            row.Invite.Id,
            row.Invite.LinkId,
            row.LinkName,
            InviteWriter.NameOf(InviteWriter.StateAt(row.Invite, now)),
            InvitePrefill.Read(row.Invite.Prefill),
            row.Invite.ExpiresAt is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
            DateTimeOffset.FromUnixTimeSeconds(row.Invite.CreatedAt)))];
    }
}
