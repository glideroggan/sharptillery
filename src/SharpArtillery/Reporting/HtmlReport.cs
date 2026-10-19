using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpArtillery.Models;

namespace SharpArtillery.Reporting;

internal static class HtmlReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.Serialize<SharpArtillery.YamlConfig.Config>(SharpArtillery.YamlConfig.Config, System.Text.Json.JsonSerializerOptions?)")]
    public static async Task CreateAsync(string outputPath, List<DataPoint> reportData, string url)
    {
        // Sort by request timeline so bucketing is stable.
        reportData.Sort((a, b) =>
            a.RequestTimeLine < b.RequestTimeLine ? -1 :
            a.RequestTimeLine == b.RequestTimeLine ? 0 : 1);

        // get content from manifest
        await using var stream =
            ReportHelper.GetContentFromManifest(ReportHelper.ReportingTemplatesLocations[ReportingTemplates.Html]);
        Debug.Assert(stream != null);

        // stream is now the template, so make a new file
        var dest = File.Create(outputPath);
        await stream.CopyToAsync(dest);
        await dest.FlushAsync();
        dest.Close();

        // add info to new file
        var content = await File.ReadAllTextAsync(dest.Name);

        // The chart library goes into the report itself, so a report opened offline still has its charts.
        await using (var chartJs = ReportHelper.GetContentFromManifest(
                         ReportHelper.ReportingTemplatesLocations[ReportingTemplates.ChartJs]))
        {
            Debug.Assert(chartJs != null);
            using var reader = new StreamReader(chartJs);
            content = content.Replace("<# CHART JS #>", await reader.ReadToEndAsync());
        }

        // add settings
        // var settingsJson = JsonSerializer.Serialize(settings, Options);
        // content = content.Replace("<# PUT SETTINGS HERE #>", settingsJson);

        // prepare data for html
        var (seriesJson, summaryJson) = PrepareReportData(reportData, url);
        content = content.Replace("<# PUT DATA HERE #>", seriesJson);
        content = content.Replace("<# PUT SUMMARY HERE #>", summaryJson);

        // write content back and we should be done
        await using var reportFile = File.OpenWrite(dest.Name);
        var byteArr = Encoding.UTF8.GetBytes(content);
        await reportFile.WriteAsync(byteArr);

        Console.Out.WriteLine("Report saved");
    }

    internal static string PrepareTimeSeriesData(List<DataPoint> requestData) =>
        PrepareReportData(requestData, "").Series;

    internal static string PrepareSummaryData(List<DataPoint> requestData, string url) =>
        PrepareReportData(requestData, url).Summary;

    private sealed class Bucket
    {
        public int Total;
        public int Errors;
        public int OkCount;
        public double OkLatencySum;
        public string? Phase;
    }

    private sealed class PhaseStats(string name)
    {
        public readonly string Name = name;
        public int Requests;
        public int Errors;
        public int OkCount;
        public double MinSeconds = double.MaxValue;
        public double MaxSeconds = double.MinValue;

        // Filled by the walk over the sorted latencies, in ascending order.
        public int OkSeen;
        public double OkLatencySum;
        public double P50;
        public double P95;
        public double P99;
    }

    // How an error is grouped in the histogram. The label is built once per group afterwards,
    // so a run with many errors does not build a string per error.
    private readonly record struct ErrorGroup(int Kind, string? ErrorType, string? ExceptionType, int Status)
    {
        public const int Timeout = 0, TypeAndException = 1, TypeOnly = 2, HttpStatus = 3, ExceptionOnly = 4, Unknown = 5;

        public static ErrorGroup Of(in DataPoint d)
        {
            if (d.IsTimeout) return new(Timeout, null, null, 0);

            if (!string.IsNullOrWhiteSpace(d.ErrorType))
            {
                return string.IsNullOrWhiteSpace(d.ExceptionType)
                    ? new(TypeOnly, d.ErrorType, null, 0)
                    : new(TypeAndException, d.ErrorType, d.ExceptionType, 0);
            }

            // Non-2xx responses are errors, but not necessarily exceptions.
            if ((int)d.Status != 0)
                return new(HttpStatus, null, null, (int)d.Status);

            if (!string.IsNullOrWhiteSpace(d.ExceptionType))
                return new(ExceptionOnly, null, d.ExceptionType, 0);

            return new(Unknown, null, null, 0);
        }

        public string Label() => Kind switch
        {
            Timeout => "timeout",
            TypeAndException => $"{ErrorType}: {ExceptionType}",
            TypeOnly => ErrorType!,
            HttpStatus => $"http_{Status}",
            ExceptionOnly => ExceptionType!,
            _ => "unknown"
        };
    }

    /// <summary>
    /// The report's time series and summary, from one pass over the results and one sort of the
    /// OK latencies. Every sum is taken in the order the earlier per-group LINQ version used
    /// (data order per second, ascending latency elsewhere), so the numbers are the same to the
    /// last digit.
    /// </summary>
    internal static (string Series, string Summary) PrepareReportData(List<DataPoint> requestData, string url)
    {
        var total = requestData.Count;
        var errors = 0;
        var timeouts = 0;
        var respondedCount = 0;
        var status2xx = 0;
        var status3xx = 0;
        var status4xx = 0;
        var status5xx = 0;
        var statusCounts = new Dictionary<int, int>();
        var errorCounts = new Dictionary<ErrorGroup, int>();
        var knownResponseLengthCount = 0;
        var responseLengthSum = 0d;
        var maxResponseLengthBytes = 0d;
        var durationSeconds = double.MinValue;
        var buckets = new Dictionary<int, Bucket>();
        var phaseIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var phases = new List<PhaseStats>();

        // The OK latencies, and the phase of each (-1 for none). Sorted together after the pass,
        // so every phase's latencies come out in ascending order too.
        var okLatencies = new double[total];
        var okPhases = new int[total];
        var okCount = 0;

        foreach (ref readonly var d in CollectionsMarshal.AsSpan(requestData))
        {
            var isError = d.IsError();
            var seconds = d.RequestTimeLine.TotalSeconds;
            var latencyMs = d.ResponseTime.TotalMilliseconds;
            if (seconds > durationSeconds) durationSeconds = seconds;

            if (d.IsTimeout) timeouts++;
            if (d.HasResponse)
            {
                respondedCount++;
                var status = (int)d.Status;
                if (status >= 200 && status < 300) status2xx++;
                else if (status >= 300 && status < 400) status3xx++;
                else if (status >= 400 && status < 500) status4xx++;
                else if (status >= 500 && status < 600) status5xx++;
                statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;

                if (!isError && d.ResponseContentLengthBytes is { } length)
                {
                    knownResponseLengthCount++;
                    responseLengthSum += length;
                    if (knownResponseLengthCount == 1 || length > maxResponseLengthBytes)
                        maxResponseLengthBytes = length;
                }
            }

            var second = (int)Math.Floor(seconds);
            if (!buckets.TryGetValue(second, out var bucket))
                buckets[second] = bucket = new Bucket();
            bucket.Total++;
            if (bucket.Phase == null && !string.IsNullOrWhiteSpace(d.PhaseName))
                bucket.Phase = d.PhaseName;

            PhaseStats? phase = null;
            var phaseNumber = -1;
            if (!string.IsNullOrWhiteSpace(d.PhaseName))
            {
                if (!phaseIndex.TryGetValue(d.PhaseName, out phaseNumber))
                {
                    phaseNumber = phases.Count;
                    phaseIndex[d.PhaseName] = phaseNumber;
                    phases.Add(new PhaseStats(d.PhaseName));
                }
                phase = phases[phaseNumber];
                phase.Requests++;
                if (seconds < phase.MinSeconds) phase.MinSeconds = seconds;
                if (seconds > phase.MaxSeconds) phase.MaxSeconds = seconds;
            }

            if (isError)
            {
                errors++;
                bucket.Errors++;
                if (phase != null) phase.Errors++;
                var group = ErrorGroup.Of(d);
                errorCounts[group] = errorCounts.GetValueOrDefault(group) + 1;
            }
            else
            {
                bucket.OkCount++;
                bucket.OkLatencySum += latencyMs;
                if (phase != null) phase.OkCount++;
                okLatencies[okCount] = latencyMs;
                okPhases[okCount] = phaseNumber;
                okCount++;
            }
        }

        Array.Sort(okLatencies, okPhases, 0, okCount);
        var sortedLatencies = new ReadOnlySpan<double>(okLatencies, 0, okCount);

        var latencySum = 0d;
        for (var i = 0; i < okCount; i++)
        {
            var latencyMs = sortedLatencies[i];
            latencySum += latencyMs;
            if (okPhases[i] < 0) continue;

            var phase = phases[okPhases[i]];
            var rank = phase.OkSeen++;
            phase.OkLatencySum += latencyMs;
            if (rank == StatsUtils.PercentileIndex(phase.OkCount, 0.50)) phase.P50 = latencyMs;
            if (rank == StatsUtils.PercentileIndex(phase.OkCount, 0.95)) phase.P95 = latencyMs;
            if (rank == StatsUtils.PercentileIndex(phase.OkCount, 0.99)) phase.P99 = latencyMs;
        }

        var series = buckets
            .OrderBy(b => b.Key)
            .Select(b => new
            {
                Time = b.Key,
                MeanLatencyMs = b.Value.OkCount > 0 ? b.Value.OkLatencySum / b.Value.OkCount : 0d,
                Rps = b.Value.Total,
                ErrorRatio = b.Value.Total > 0 ? (double)b.Value.Errors / b.Value.Total : 0d,
                Phase = b.Value.Phase ?? "default"
            })
            .ToList();

        var statusHistogram = statusCounts
            .OrderBy(s => s.Key)
            .Select(s => new
            {
                StatusCode = s.Key,
                Count = s.Value,
                Ratio = respondedCount > 0 ? (double)s.Value / respondedCount : 0d
            })
            .ToList();

        // Groups of different kinds can share a label ("timeout"), so they are merged by label.
        var errorHistogram = errorCounts
            .GroupBy(e => e.Key.Label(), e => e.Value, StringComparer.Ordinal)
            .Select(g => (Error: g.Key, Count: g.Sum()))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Error, StringComparer.Ordinal)
            .Select(e => new
            {
                e.Error,
                e.Count,
                Ratio = errors > 0 ? (double)e.Count / errors : 0d
            })
            .ToList();

        if (total == 0 || durationSeconds <= 0d) durationSeconds = 0d;

        var phaseBreakdown = phases
            .Select(p =>
            {
                var phaseDuration = p.MaxSeconds - p.MinSeconds;
                // Add a small epsilon to avoid division by zero for very short phases
                if (phaseDuration < 0.1) phaseDuration = 1.0;

                return new
                {
                    Phase = p.Name,
                    p.Requests,
                    p.Errors,
                    ErrorRatio = (double)p.Errors / p.Requests,
                    DurationSeconds = phaseDuration,
                    AverageRps = phaseDuration > 0 ? p.Requests / phaseDuration : 0d,
                    MeanLatencyMs = p.OkCount > 0 ? p.OkLatencySum / p.OkCount : 0d,
                    P50LatencyMs = p.P50,
                    P95LatencyMs = p.P95,
                    P99LatencyMs = p.P99
                };
            })
            .ToList();

        var summary = new
        {
            Endpoint = url,
            Requests = total,
            Ok = total - errors,
            Errors = errors,
            ErrorRatio = total > 0 ? (double)errors / total : 0d,
            Timeouts = timeouts,
            Status2xx = status2xx,
            Status3xx = status3xx,
            Status4xx = status4xx,
            Status5xx = status5xx,
            StatusHistogram = statusHistogram,
            ErrorHistogram = errorHistogram,
            KnownResponseLengthCount = knownResponseLengthCount,
            MeanResponseLengthBytes = knownResponseLengthCount > 0 ? responseLengthSum / knownResponseLengthCount : 0d,
            MaxResponseLengthBytes = maxResponseLengthBytes,
            DurationSeconds = durationSeconds,
            AverageRps = durationSeconds > 0 ? total / durationSeconds : 0d,
            MeanLatencyMs = okCount > 0 ? latencySum / okCount : 0d,
            MaxLatencyMs = okCount > 0 ? sortedLatencies[^1] : 0d,
            P50LatencyMs = StatsUtils.Percentile(sortedLatencies, 0.50),
            P90LatencyMs = StatsUtils.Percentile(sortedLatencies, 0.90),
            P95LatencyMs = StatsUtils.Percentile(sortedLatencies, 0.95),
            P99LatencyMs = StatsUtils.Percentile(sortedLatencies, 0.99),
            PhaseBreakdown = phaseBreakdown
        };

        return (JsonSerializer.Serialize(series, Options), JsonSerializer.Serialize(summary, Options));
    }
}
