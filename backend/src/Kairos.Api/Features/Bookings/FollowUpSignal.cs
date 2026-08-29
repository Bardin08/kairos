using System.Threading.Channels;

namespace Kairos.Api.Features.Bookings;

/// <summary>
/// Wakes the follow-up worker the moment a row is committed, so a booker's confirmation leaves in
/// about a second rather than in up to ten.
/// </summary>
/// <remarks>
/// An optimisation, and never the mechanism. One slot, and a write into a full one is dropped, so
/// a burst of bookings signals once and one pass of the queue answers all of them. A nudge lost —
/// to a crash, to a restart, to that full slot — costs a delay bounded by the worker's own timer
/// and never costs a message, because the row in the database is the queue and this is not.
///
/// Nothing here is durable and nothing is meant to be. That is what makes it safe to hold in
/// memory in a process that can be killed between the commit and the nudge.
/// </remarks>
public sealed class FollowUpSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>
    /// Call after the commit, never before it. The worker reads on its own connection, so a nudge
    /// sent inside the transaction races a pass that cannot yet see the row — costing the delay
    /// the nudge exists to remove.
    /// </summary>
    public void Nudge() => _channel.Writer.TryWrite(default);

    /// <summary>
    /// Waits for at least one nudge, then takes every nudge that has arrived. Drained rather than
    /// read singly: the nudges carry nothing and are indistinguishable, so one pass of the queue
    /// answers every one that preceded it.
    /// </summary>
    /// <remarks>
    /// Nothing ever completes the channel's writer, so the wait either returns or is cancelled —
    /// <see cref="ChannelReader{T}.WaitToReadAsync"/> throws <see cref="OperationCanceledException"/>
    /// on the token, which is how the worker learns to stop.
    /// </remarks>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _channel.Reader.WaitToReadAsync(cancellationToken);

        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}
