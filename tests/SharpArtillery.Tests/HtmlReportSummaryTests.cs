using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using SharpArtillery.Models;
using SharpArtillery.Reporting;
using Xunit;

namespace SharpArtillery.Tests;

public class HtmlReportSummaryTests
{
    private static DataPoint Ok(double latencyMs, double timelineSeconds, string phase, long? bytes) => new()
    {
        Status = HttpStatusCode.OK,
        HasResponse = true,
        ResponseTime = TimeSpan.FromMilliseconds(latencyMs),
        RequestTimeLine = TimeSpan.FromSeconds(timelineSeconds),
        PhaseName = phase,
        ResponseContentLengthBytes = bytes
    };

    // Two OK requests in phase A, then a 404, a timeout and an exception (no response) in phase B.
    private static List<DataPoint> Sample() =>
    [
        Ok(10, 1, "A", 100),
        Ok(20, 2, "A", 300),
        new DataPoint
        {
            Status = HttpStatusCode.NotFound,
            HasResponse = true,
            ResponseTime = TimeSpan.FromMilliseconds(5),
            RequestTimeLine = TimeSpan.FromSeconds(3),
            PhaseName = "B"
        },
        new DataPoint
        {
            Status = 0,
            IsTimeout = true,
            ErrorType = "timeout",
            ExceptionType = "System.Threading.Tasks.TaskCanceledException",
            Error = "timed out",
            RequestTimeLine = TimeSpan.FromSeconds(4),
            PhaseName = "B"
        },
        new DataPoint
        {
            Status = HttpStatusCode.InternalServerError,
            ErrorType = "http_request_exception",
            ExceptionType = "System.Net.Http.HttpRequestException",
            Error = "connection refused",
            RequestTimeLine = TimeSpan.FromSeconds(4),
            PhaseName = "B"
        }
    ];

    private static JsonElement Summary(List<DataPoint> data) =>
        JsonDocument.Parse(HtmlReport.PrepareSummaryData(data, "http://stub.test/x")).RootElement;

    [Fact]
    public void Totals_count_every_request()
    {
        var s = Summary(Sample());

        Assert.Equal("http://stub.test/x", s.GetProperty("endpoint").GetString());
        Assert.Equal(5, s.GetProperty("requests").GetInt32());
        Assert.Equal(2, s.GetProperty("ok").GetInt32());
        Assert.Equal(3, s.GetProperty("errors").GetInt32());
        Assert.Equal(0.6, s.GetProperty("errorRatio").GetDouble(), 10);
        Assert.Equal(1, s.GetProperty("timeouts").GetInt32());
    }

    [Fact]
    public void Status_classes_count_only_requests_with_a_response()
    {
        var s = Summary(Sample());

        Assert.Equal(2, s.GetProperty("status2xx").GetInt32());
        Assert.Equal(0, s.GetProperty("status3xx").GetInt32());
        Assert.Equal(1, s.GetProperty("status4xx").GetInt32());
        // The exception's 500 has no response, so it is not a 5xx here.
        Assert.Equal(0, s.GetProperty("status5xx").GetInt32());

        var histogram = s.GetProperty("statusHistogram").EnumerateArray()
            .Select(e => (e.GetProperty("statusCode").GetInt32(), e.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal([(200, 2), (404, 1)], histogram);
    }

    [Fact]
    public void Error_histogram_keys_by_kind_sorted_by_count_then_name()
    {
        var s = Summary(Sample());

        var histogram = s.GetProperty("errorHistogram").EnumerateArray()
            .Select(e => (e.GetProperty("error").GetString(), e.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal(
            [
                ("http_404", 1),
                ("http_request_exception: System.Net.Http.HttpRequestException", 1),
                ("timeout", 1)
            ],
            histogram);
    }

    [Fact]
    public void Latency_and_size_use_ok_requests_only()
    {
        var s = Summary(Sample());

        Assert.Equal(2, s.GetProperty("knownResponseLengthCount").GetInt32());
        Assert.Equal(200, s.GetProperty("meanResponseLengthBytes").GetDouble());
        Assert.Equal(300, s.GetProperty("maxResponseLengthBytes").GetDouble());
        Assert.Equal(15, s.GetProperty("meanLatencyMs").GetDouble());
        Assert.Equal(20, s.GetProperty("maxLatencyMs").GetDouble());
        Assert.Equal(10, s.GetProperty("p50LatencyMs").GetDouble());
        Assert.Equal(20, s.GetProperty("p90LatencyMs").GetDouble());
        Assert.Equal(20, s.GetProperty("p95LatencyMs").GetDouble());
        Assert.Equal(20, s.GetProperty("p99LatencyMs").GetDouble());
    }

    [Fact]
    public void Duration_is_the_last_timeline_point()
    {
        var s = Summary(Sample());

        Assert.Equal(4, s.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1.25, s.GetProperty("averageRps").GetDouble());
    }

    [Fact]
    public void Phase_breakdown_has_one_entry_per_phase()
    {
        var phases = Summary(Sample()).GetProperty("phaseBreakdown").EnumerateArray().ToList();

        Assert.Equal(["A", "B"], phases.Select(p => p.GetProperty("phase").GetString()));

        var a = phases[0];
        Assert.Equal(2, a.GetProperty("requests").GetInt32());
        Assert.Equal(0, a.GetProperty("errors").GetInt32());
        Assert.Equal(1, a.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(2, a.GetProperty("averageRps").GetDouble());
        Assert.Equal(15, a.GetProperty("meanLatencyMs").GetDouble());
        Assert.Equal(10, a.GetProperty("p50LatencyMs").GetDouble());
        Assert.Equal(20, a.GetProperty("p95LatencyMs").GetDouble());

        var b = phases[1];
        Assert.Equal(3, b.GetProperty("requests").GetInt32());
        Assert.Equal(3, b.GetProperty("errors").GetInt32());
        Assert.Equal(1.0, b.GetProperty("errorRatio").GetDouble());
        Assert.Equal(0, b.GetProperty("meanLatencyMs").GetDouble());
    }

    [Fact]
    public void A_phase_shorter_than_a_tenth_of_a_second_counts_as_one_second()
    {
        var data = new List<DataPoint> { Ok(10, 1, "A", null), Ok(10, 1.05, "A", null) };

        var phase = Summary(data).GetProperty("phaseBreakdown")[0];

        Assert.Equal(1, phase.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(2, phase.GetProperty("averageRps").GetDouble());
    }

    [Fact]
    public void An_empty_run_gives_zeros()
    {
        var s = Summary([]);

        Assert.Equal(0, s.GetProperty("requests").GetInt32());
        Assert.Equal(0, s.GetProperty("errorRatio").GetDouble());
        Assert.Equal(0, s.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(0, s.GetProperty("averageRps").GetDouble());
        Assert.Equal(0, s.GetProperty("p50LatencyMs").GetDouble());
        Assert.Empty(s.GetProperty("phaseBreakdown").EnumerateArray());
    }
}
