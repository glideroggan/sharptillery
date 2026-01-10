using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpArtillery;

/// <summary>
/// Per-client rate limiter with jitter for realistic load distribution.
/// Each client owns its own limiter and paces its requests independently.
/// </summary>
internal sealed class ClientRateLimiter
{
    private readonly double _baseIntervalMs;
    private readonly double _jitterRangeMs;
    private readonly TimeSpan _initialDelay;
    private readonly Random _random;
    private bool _firstRequest = true;

    /// <summary>
    /// Creates a rate limiter for a single client.
    /// </summary>
    /// <param name="totalRps">Total target RPS across all clients</param>
    /// <param name="clientCount">Number of clients sharing the load</param>
    /// <param name="clientIndex">This client's index (0-based) for stagger offset</param>
    /// <param name="jitterPercent">Jitter as percentage of interval (default 20%)</param>
    public ClientRateLimiter(int totalRps, int clientCount, int clientIndex, double jitterPercent = 0.20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalRps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clientCount);
        if (clientIndex < 0 || clientIndex >= clientCount) throw new ArgumentOutOfRangeException(nameof(clientIndex));

        // Each client's share of the total RPS
        var clientRps = (double)totalRps / clientCount;

        // Interval between this client's requests
        _baseIntervalMs = 1000.0 / clientRps;

        // Jitter range (±jitterPercent of interval)
        _jitterRangeMs = _baseIntervalMs * jitterPercent;

        // Stagger: spread clients evenly across the first interval
        // Client 0 starts immediately, Client N-1 starts at (N-1)/N * interval
        var staggerMs = (_baseIntervalMs / clientCount) * clientIndex;
        _initialDelay = TimeSpan.FromMilliseconds(staggerMs);

        // Each client gets its own random for thread safety
        _random = new Random(Environment.TickCount ^ clientIndex);
    }

    /// <summary>
    /// Wait until this client is allowed to send the next request.
    /// First call applies the stagger delay, subsequent calls apply interval + jitter.
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;

        if (_firstRequest)
        {
            _firstRequest = false;
            delay = _initialDelay;
        }
        else
        {
            // Base interval ± jitter
            var jitter = (_random.NextDouble() * 2 - 1) * _jitterRangeMs; // Range: -jitter to +jitter
            var intervalMs = Math.Max(1, _baseIntervalMs + jitter); // Never go below 1ms
            delay = TimeSpan.FromMilliseconds(intervalMs);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
