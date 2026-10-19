using System;
using System.Collections.Generic;
using System.Net;
using SharpArtillery.Models;
using SharpArtillery.Reporting;
using Xunit;

namespace SharpArtillery.Tests;

// The report's numbers come from one pass over the results and one sorted latency array
// (sharptillery#583 S5). They must be the same numbers the LINQ version gave, to the last digit.
public class HtmlReportOnePassTests
{
    private const string Url = "http://stub.test/x";

    private static readonly string?[] Phases = ["A", "B", "Warm up", null, "", " "];

    // Every kind of result the clients write: OK with and without a length, a 2xx with an error
    // message, 1xx/3xx/4xx/5xx answers, timeouts, exceptions with and without a type, and a
    // status-0 result with nothing else set. Latencies have fractions so the sums have rounding.
    private static List<DataPoint> RandomRun(int count, int seed)
    {
        var random = new Random(seed);
        var data = new List<DataPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var point = new DataPoint
            {
                RequestTimeLine = TimeSpan.FromSeconds(random.NextDouble() * 30),
                ResponseTime = TimeSpan.FromMilliseconds(random.NextDouble() * 400),
                PhaseName = Phases[random.Next(Phases.Length)]
            };
            switch (random.Next(12))
            {
                case <= 5:
                    point.Status = HttpStatusCode.OK;
                    point.HasResponse = true;
                    point.ResponseContentLengthBytes = random.Next(3) == 0 ? null : random.Next(0, 5000);
                    break;
                case 6:
                    point.Status = HttpStatusCode.Created;
                    point.HasResponse = true;
                    point.Error = random.Next(2) == 0 ? "boom" : "";
                    break;
                case 7:
                    point.Status = (HttpStatusCode)new[] { 101, 302, 404, 429, 503 }[random.Next(5)];
                    point.HasResponse = true;
                    point.ResponseContentLengthBytes = random.Next(100);
                    break;
                case 8:
                    point.IsTimeout = true;
                    point.ErrorType = "timeout";
                    point.ExceptionType = "System.Threading.Tasks.TaskCanceledException";
                    point.Error = "timed out";
                    break;
                case 9:
                    point.Status = HttpStatusCode.InternalServerError;
                    point.ErrorType = "http_request_exception";
                    point.ExceptionType = random.Next(2) == 0 ? "System.Net.Http.HttpRequestException" : " ";
                    point.Error = "connection refused";
                    break;
                case 10:
                    point.ExceptionType = "System.IO.IOException";
                    point.Error = "reset";
                    break;
                default:
                    // Status 0 and nothing else: an error of unknown kind.
                    break;
            }
            data.Add(point);
        }

        // As HtmlReport.CreateAsync does before it computes anything.
        data.Sort((a, b) => a.RequestTimeLine.CompareTo(b.RequestTimeLine));
        return data;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(7, 3)]
    [InlineData(5_000, 4)]
    [InlineData(50_000, 5)]
    public void The_summary_is_the_same_as_the_linq_version(int count, int seed)
    {
        var data = RandomRun(count, seed);

        Assert.Equal(ReferenceReport.Summary(data, Url), HtmlReport.PrepareSummaryData(data, Url));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(7, 3)]
    [InlineData(5_000, 4)]
    [InlineData(50_000, 5)]
    public void The_time_series_is_the_same_as_the_linq_version(int count, int seed)
    {
        var data = RandomRun(count, seed);

        Assert.Equal(ReferenceReport.TimeSeries(data), HtmlReport.PrepareTimeSeriesData(data));
    }

    // The LINQ version copied the results several times over (a list of the answered ones, a
    // group per phase and per second), so a large run needed several times its own size again
    // to write the report. The one pass needs a latency array and a phase index per OK result.
    [Fact]
    public void The_summary_allocates_little_more_than_the_latency_array()
    {
        const int count = 200_000;
        var data = RandomRun(count, 6);
        HtmlReport.PrepareSummaryData(RandomRun(10, 7), Url); // JIT and serializer caches

        var before = GC.GetAllocatedBytesForCurrentThread();
        HtmlReport.PrepareSummaryData(data, Url);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32L * count, $"allocated {allocated:N0} bytes for {count:N0} results");
    }

    [Fact]
    public void The_time_series_allocates_little_more_than_the_latency_array()
    {
        const int count = 200_000;
        var data = RandomRun(count, 8);
        HtmlReport.PrepareTimeSeriesData(RandomRun(10, 9));

        var before = GC.GetAllocatedBytesForCurrentThread();
        HtmlReport.PrepareTimeSeriesData(data);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32L * count, $"allocated {allocated:N0} bytes for {count:N0} results");
    }
}
