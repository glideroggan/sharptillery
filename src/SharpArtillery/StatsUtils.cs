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
    /// Sorts <paramref name="points"/> by when each answer arrived (its timeline point). The console's
    /// first error is the first one in list order, so the results are kept in this order after a run.
    /// </summary>
    public static void SortByTimeline(List<DataPoint> points) =>
        points.Sort((a, b) => a.RequestTimeLine.CompareTo(b.RequestTimeLine));

    /// <summary>
    /// Requests per second over <paramref name="elapsed"/>, rounded down; 0 when no time has passed.
    /// </summary>
    public static int AverageRps(long requests, TimeSpan elapsed) =>
        elapsed > TimeSpan.Zero ? (int)(requests / elapsed.TotalSeconds) : 0;
}
