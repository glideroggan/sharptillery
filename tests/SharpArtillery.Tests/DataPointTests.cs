using System.Net;
using System.Runtime.CompilerServices;
using SharpArtillery.Models;
using Xunit;

namespace SharpArtillery.Tests;

public class DataPointTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(204, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(199, true)]
    [InlineData(404, true)]
    [InlineData(500, true)]
    [InlineData(0, true)]   // no response, e.g. a timeout
    public void IsError_is_true_outside_2xx(int status, bool expected)
    {
        var dp = new DataPoint { Status = (HttpStatusCode)status };

        Assert.Equal(expected, dp.IsError());
    }

    [Fact]
    public void IsError_is_true_for_a_2xx_with_an_error_message()
    {
        var dp = new DataPoint { Status = HttpStatusCode.OK, Error = "boom" };

        Assert.True(dp.IsError());
    }

    [Fact]
    public void IsError_ignores_an_empty_error_message()
    {
        var dp = new DataPoint { Status = HttpStatusCode.OK, Error = "" };

        Assert.False(dp.IsError());
    }

    // A run keeps every result in memory until the report is written, so the struct's size is
    // most of the tool's peak memory. 72 bytes measured with Unsafe.SizeOf after Rps was removed
    // (sharptillery#583 slice 6); a field added later must raise this limit on purpose.
    [Fact]
    public void A_data_point_is_at_most_72_bytes()
    {
        Assert.InRange(Unsafe.SizeOf<DataPoint>(), 1, 72);
    }
}
