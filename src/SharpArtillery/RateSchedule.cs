using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpArtillery;

/// <summary>
/// The <c>-r</c> schedule of one phase, shared by all its clients: slot n is due at the phase start + n / rps.
/// A free client takes the next slot and waits until it is due. The wait is measured from the planned slot,
/// never from the client's own last request, so a slow answer does not lower the rate as long as some client
/// is free to take the next slot. When every client is busy the slots fall due without a taker and are sent
/// as soon as clients come free, so the run catches up instead of losing them.
/// </summary>
internal sealed class RateSchedule
{
    /// <summary>
    /// Share of the due slots that may be missing before <see cref="FellBehind"/> says so; a few slots are always
    /// in hand-over between a response and the next take.
    /// </summary>
    private const double Tolerance = 0.05;

    private readonly long _rps;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedAt;

    // Shared by every client of the phase; touched only through Interlocked/Volatile.
    private long _lastSlot = -1;
    private long _lastTakenAtTicks;

    /// <param name="rps">Target requests per second for the phase.</param>
    /// <param name="timeProvider">The run's clock; slot 0 is due at <paramref name="startedAt"/>.</param>
    /// <param name="startedAt">The phase start, a timestamp of <paramref name="timeProvider"/>.</param>
    public RateSchedule(int rps, TimeProvider timeProvider, long startedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rps);
        _rps = rps;
        _timeProvider = timeProvider;
        _startedAt = startedAt;
    }

    public int Rps => (int)_rps;

    /// <summary>When <paramref name="slot"/> is due, from the phase start.</summary>
    public TimeSpan DueAt(long slot) => TimeSpan.FromTicks(slot * TimeSpan.TicksPerSecond / _rps);

    /// <summary>Slots taken so far, including the ones whose client is still waiting for them.</summary>
    public long SlotsTaken => Volatile.Read(ref _lastSlot) + 1;

    /// <summary>
    /// True when, at the last take, fewer slots had been taken than had fallen due: the clients could not keep
    /// up with the target. Measured at the last take rather than at the phase end, so the answers still coming
    /// in after the last request of a max-requests phase do not count as falling behind.
    /// </summary>
    public bool FellBehind
    {
        get
        {
            var taken = SlotsTaken;
            if (taken == 0) return false;
            var lastTakenAt = Volatile.Read(ref _lastTakenAtTicks);
            var due = lastTakenAt * _rps / TimeSpan.TicksPerSecond + 1;
            return taken < due * (1 - Tolerance);
        }
    }

    /// <summary>Takes the next slot and waits until it is due; returns at once when it is already due.</summary>
    public async ValueTask WaitForSlotAsync(CancellationToken cancellationToken)
    {
        var slot = Interlocked.Increment(ref _lastSlot);
        var now = _timeProvider.GetElapsedTime(_startedAt);
        RecordTake(now.Ticks);

        var wait = DueAt(slot) - now;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private void RecordTake(long ticks)
    {
        // Clients take slots on several threads; the latest take wins.
        var seen = Volatile.Read(ref _lastTakenAtTicks);
        while (ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref _lastTakenAtTicks, ticks, seen);
            if (previous == seen) return;
            seen = previous;
        }
    }
}
