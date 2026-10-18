using System.Collections.Generic;
using System.Linq;
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
}
