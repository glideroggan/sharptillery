using System;
using System.Collections.Generic;

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
    public static double Percentile(List<double> sortedValues, double p)
    {
        if (sortedValues.Count == 0)
            return 0;

        var idx = (int)Math.Ceiling(p * sortedValues.Count) - 1;

        if (idx < 0)
            idx = 0;
        if (idx >= sortedValues.Count)
            idx = sortedValues.Count - 1;

        return sortedValues[idx];
    }
}
