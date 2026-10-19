using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpArtillery.Models;

namespace SharpArtillery;

/// <summary>
/// Shared statistical utility methods.
/// </summary>
internal static class StatsUtils
{
    /// <summary>
    /// Calculates the percentile value from a pre-sorted list.
    /// Uses the "ceiling - 1" method (nearest-rank percentile).
    /// </summary>
    /// <param name="sortedValues">A sorted list of values (ascending order).</param>
    /// <param name="p">The percentile to calculate (0.0 to 1.0, e.g., 0.95 for p95).</param>
    /// <returns>The value at the given percentile, or 0 if the list is empty.</returns>
    public static double Percentile(List<double> sortedValues, double p) =>
        Percentile(CollectionsMarshal.AsSpan(sortedValues), p);

    /// <inheritdoc cref="Percentile(List{double}, double)"/>
    public static double Percentile(ReadOnlySpan<double> sortedValues, double p) =>
        sortedValues.IsEmpty ? 0 : sortedValues[PercentileIndex(sortedValues.Length, p)];

    /// <summary>
    /// The index <see cref="Percentile(List{double}, double)"/> reads in a sorted list of
    /// <paramref name="count"/> values (at least one).
    /// </summary>
    public static int PercentileIndex(int count, double p)
    {
        var idx = (int)Math.Ceiling(p * count) - 1;

        if (idx < 0)
            idx = 0;
        if (idx >= count)
            idx = count - 1;

        return idx;
    }

    /// <summary>
    /// Sorts <paramref name="points"/> by their timeline point (when each answer arrived) and sets
    /// every point's <see cref="DataPoint.Rps"/> to the answers in the one second ending at that
    /// point, <c>(time - 1 s, time]</c>. One pass with a sliding window, so it is O(n) after the sort.
    /// </summary>
    public static void AssignRps(List<DataPoint> points)
    {
        points.Sort((a, b) => a.RequestTimeLine.CompareTo(b.RequestTimeLine));

        var left = 0;
        for (var right = 0; right < points.Count; right++)
        {
            var point = points[right];
            var windowStart = point.RequestTimeLine - TimeSpan.FromSeconds(1);

            while (left <= right && points[left].RequestTimeLine <= windowStart)
                left++;

            point.Rps = right - left + 1;
            points[right] = point;
        }
    }

    /// <summary>
    /// Requests per second over <paramref name="elapsed"/>, rounded down; 0 when no time has passed.
    /// </summary>
    public static int AverageRps(long requests, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? (int)(requests / elapsed.TotalSeconds) : 0;
}
