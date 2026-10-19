using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace SharpArtillery.Tests.Stubs;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also keeps its one-shot timers that have not fired yet. A rate test
/// advances the clock only once every client waits on it (on its schedule slot or in the stub's delay) and
/// no timer that is already due is still waiting to fire, so each step sees the same state on every run,
/// however the thread pool schedules the clients.
/// </summary>
internal sealed class ParkingClock : TimeProvider
{
    // Each pending one-shot timer with the time it is due.
    private readonly ConcurrentDictionary<OneShot, DateTimeOffset> _pending = new();

    public FakeTimeProvider Fake { get; } = new();

    public override long GetTimestamp() => Fake.GetTimestamp();

    public override long TimestampFrequency => Fake.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => Fake.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => Fake.LocalTimeZone;

    /// <summary>One-shot timers created on this clock that have neither fired nor been disposed.</summary>
    public int PendingOneShots => _pending.Count;

    /// <summary>
    /// True when at least <paramref name="waits"/> one-shot timers are pending and none of them is due yet.
    /// FakeTimeProvider fires a due timer on whichever thread is already firing timers, so a timer can still be
    /// due after <see cref="Advance"/> has returned; the clock must not move on before it has fired. A due timer
    /// is fired from here as well, in case the thread that was firing timers had already finished its round.
    /// </summary>
    public bool Parked(int waits)
    {
        var now = Fake.GetUtcNow();
        if (_pending.Values.Any(due => due <= now))
        {
            Fake.Advance(TimeSpan.Zero);
            return false;
        }

        return _pending.Count >= waits;
    }

    public void Advance(TimeSpan delta) => Fake.Advance(delta);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Periodic timers (the progress ticker) and unarmed ones are not waits a client can be parked on.
        if (period != Timeout.InfiniteTimeSpan || dueTime == Timeout.InfiniteTimeSpan)
            return Fake.CreateTimer(callback, state, dueTime, period);

        // The timer is counted only once it exists. Until then its client is not parked, so the test cannot
        // advance the clock in between, and the due time read here is the one the fake timer computes.
        var due = Fake.GetUtcNow() + dueTime;
        var timer = new OneShot(this);
        timer.Inner = Fake.CreateTimer(s =>
        {
            timer.Release();
            callback(s);
        }, state, dueTime, period);
        timer.Arm(due);
        return timer;
    }

    private sealed class OneShot(ParkingClock clock) : ITimer
    {
        private readonly object _gate = new();
        private bool _released;

        public ITimer? Inner { get; set; }

        /// <summary>Counts the timer as pending, unless it has already fired (a due time of zero can).</summary>
        public void Arm(DateTimeOffset due)
        {
            lock (_gate)
            {
                if (!_released) clock._pending[this] = due;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                clock._pending.TryRemove(this, out _);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => Inner?.Change(dueTime, period) ?? false;

        public void Dispose()
        {
            Release();
            Inner?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
