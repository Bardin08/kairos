using Kairos.Api.Scheduling;

namespace Kairos.Api.Availability;

/// <summary>
/// One provider of intervals the organizer is not free in. Part A registers a single
/// implementation and Part B adds Google as a second one; nothing that consumes this changes,
/// because every source answers the same question over the same window and the answers
/// concatenate into the one conflict pool <see cref="SlotGenerator"/> reads.
/// </summary>
public interface IBusySource
{
    /// <summary>
    /// Every block that can affect the half-open UTC <paramref name="window"/>, each carrying its
    /// own margin. Never pre-padded — <see cref="SlotGenerator"/> pads by the larger of the
    /// block's margin and the querying link's, and padding here would apply both.
    /// </summary>
    public Task<IReadOnlyList<BusyBlock>> GetBusyAsync(Interval window, CancellationToken cancellationToken);
}
