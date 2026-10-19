using System;
using System.Collections.Generic;
using System.Linq;
using SharpArtillery.Models;
using Xunit;

namespace SharpArtillery.Tests;

public class StatsUtilsTests
{
    private static readonly List<double> OneToTen = Enumerable.Range(1, 10).Select(i => (double)i).ToList();

    [Fact]
    public void Percentile_of_an_empty_list_is_zero()
    {
        Assert.Equal(0, StatsUtils.Percentile(new List<double>(), 0.95));
    }

    [Theory]
    [InlineData(0.50, 5)]   // ceil(5.0) - 1 = index 4
    [InlineData(0.90, 9)]   // ceil(9.0) - 1 = index 8
    [InlineData(0.95, 10)]  // ceil(9.5) - 1 = index 9
    [InlineData(0.99, 10)]
    [InlineData(0.11, 2)]   // ceil(1.1) - 1 = index 1
    public void Percentile_uses_nearest_rank(double p, double expected)
    {
        Assert.Equal(expected, StatsUtils.Percentile(OneToTen, p));
    }

    [Theory]
    [InlineData(0.0, 1)]    // index -1 is clamped to the first value
    [InlineData(1.0, 10)]
    [InlineData(1.5, 10)]   // index past the end is clamped to the last value
    public void Percentile_clamps_to_the_list(double p, double expected)
    {
        Assert.Equal(expected, StatsUtils.Percentile(OneToTen, p));
    }

    [Fact]
    public void Percentile_of_one_value_is_that_value()
    {
        Assert.Equal(42, StatsUtils.Percentile(new List<double> { 42 }, 0.5));
    }

    private static DataPoint At(double seconds) => new()
    {
        RequestTimeLine = TimeSpan.FromSeconds(seconds)
    };

    // Blast and flow mode share this one helper, so the report counts both the same way.
    [Fact]
    [Trait("slice", "4")]
    public void Rps_counts_the_answers_in_the_second_ending_at_each_point()
    {
        // Out of order on purpose: the helper sorts by arrival.
        var points = new List<DataPoint> { At(1.0), At(0.0), At(0.5), At(2.0), At(1.5) };

        StatsUtils.AssignRps(points);

        Assert.Equal(new[] { 0.0, 0.5, 1.0, 1.5, 2.0 },
            points.Select(p => p.RequestTimeLine.TotalSeconds));
        // The window is (t - 1 s, t]: a point exactly one second earlier is outside it.
        Assert.Equal(new[] { 1, 2, 2, 2, 2 }, points.Select(p => p.Rps));
    }

    [Fact]
    [Trait("slice", "4")]
    public void Rps_of_no_points_leaves_the_list_empty()
    {
        var points = new List<DataPoint>();

        StatsUtils.AssignRps(points);

        Assert.Empty(points);
    }

    [Theory]
    [InlineData(10, 2.0, 5)]
    [InlineData(7, 2.0, 3)]
    [InlineData(5, 0.0, 0)]
    [InlineData(0, 1.0, 0)]
    [Trait("slice", "4")]
    public void Average_rps_rounds_down_and_is_zero_without_time(long requests, double seconds, int expected)
    {
        Assert.Equal(expected, StatsUtils.AverageRps(requests, TimeSpan.FromSeconds(seconds)));
    }
}
