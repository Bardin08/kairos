using Kairos.Api.Persistence;

using Microsoft.EntityFrameworkCore;

using NodaTime;

namespace Kairos.Api.Features.Invites;

/// <summary>
/// Reading a link's invites for the operator. Separate from <see cref="InviteWriter"/> because
/// this is the query side of the same rows, and because the fold it applies cannot run where the
/// query does: <see cref="InviteWriter.StateAt"/> needs a clock, which SQLite has no access to.
/// </summary>
public sealed class InviteReader(KairosDbContext context, IClock clock)
{
    public async Task<InviteView[]> ForLinkAsync(Guid linkId, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant();

        // AsNoTracking, then folded once the rows are in hand. The state is not a column, so it
        // is not something this Where could have filtered on; a list already holding its rows has
        // no reason to ask the database a question C# can answer about them.
        var invites = await context.PersonalInvites.AsNoTracking()
            .Where(invite => invite.LinkId == linkId)
            .OrderByDescending(invite => invite.CreatedAt)
            .ToListAsync(cancellationToken);

        return [.. invites.Select(invite => new InviteView(
            invite.Id,
            InviteWriter.StateAt(invite, now).ToString().ToLowerInvariant(),
            InvitePrefill.Read(invite.Prefill),
            invite.ExpiresAt is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
            DateTimeOffset.FromUnixTimeSeconds(invite.CreatedAt)))];
    }
}
