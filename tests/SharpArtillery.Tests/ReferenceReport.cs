using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SharpArtillery.Models;

namespace SharpArtillery.Tests;

/// <summary>
/// The HTML report's numbers as the LINQ version computed them before the one-pass rewrite
/// (sharptillery#583 S5), kept verbatim so the new code can be compared with it on the same data.
/// </summary>
internal static class ReferenceReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string TimeSeries(List<DataPoint> requestData)
    {
        var buckets = requestData
            .GroupBy(d => (int)Math.Floor(d.RequestTimeLine.TotalSeconds))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var total = g.Count();
                var errors = g.Count(d => d.IsError());
                var okLatencies = g.Where(d => !d.IsError()).Select(d => d.ResponseTime.TotalMilliseconds).ToList();
                var meanLatency = okLatencies.Count > 0 ? okLatencies.Average() : 0d;

                var phaseName = g.Select(d => d.PhaseName).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                                ?? "default";

                return new
                {
                    Time = g.Key,
                    MeanLatencyMs = meanLatency,
                    Rps = total,
                    ErrorRatio = total > 0 ? (double)errors / total : 0d,
                    Phase = phaseName
                };
            });

        return JsonSerializer.Serialize(buckets, Options);
    }

    public static string Summary(List<DataPoint> requestData, string url)
    {
        var total = requestData.Count;
        var errors = requestData.Count(d => d.IsError());
        var ok = total - errors;

        var responded = requestData.Where(d => d.HasResponse).ToList();
        var status2xx = responded.Count(d => (int)d.Status >= 200 && (int)d.Status < 300);
        var status3xx = responded.Count(d => (int)d.Status >= 300 && (int)d.Status < 400);
        var status4xx = responded.Count(d => (int)d.Status >= 400 && (int)d.Status < 500);
        var status5xx = responded.Count(d => (int)d.Status >= 500 && (int)d.Status < 600);

        var respondedCount = responded.Count;
        var statusHistogram = responded
            .GroupBy(d => (int)d.Status)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                StatusCode = g.Key,
                Count = g.Count(),
                Ratio = respondedCount > 0 ? (double)g.Count() / respondedCount : 0d
            })
            .ToList();

        static string? GetErrorKey(DataPoint d)
        {
            if (!d.IsError()) return null;
            if (d.IsTimeout) return "timeout";

            if (!string.IsNullOrWhiteSpace(d.ErrorType))
            {
                if (!string.IsNullOrWhiteSpace(d.ExceptionType))
                    return $"{d.ErrorType}: {d.ExceptionType}";
                return d.ErrorType;
            }

            if ((int)d.Status != 0)
                return $"http_{(int)d.Status}";

            if (!string.IsNullOrWhiteSpace(d.ExceptionType))
                return d.ExceptionType;

            return "unknown";
        }

        var errorHistogram = requestData
            .Select(GetErrorKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .GroupBy(k => k!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                Error = g.Key,
                Count = g.Count(),
                Ratio = errors > 0 ? (double)g.Count() / errors : 0d
            })
            .ToList();

        var timeouts = requestData.Count(d => d.IsTimeout);

        var okKnownLengths = requestData
            .Where(d => d.HasResponse && !d.IsError() && d.ResponseContentLengthBytes.HasValue)
            .Select(d => (double)d.ResponseContentLengthBytes!.Value)
            .ToList();
        okKnownLengths.Sort();
        var knownResponseLengthCount = okKnownLengths.Count;
        var meanResponseLengthBytes = knownResponseLengthCount > 0 ? okKnownLengths.Average() : 0d;
        var maxResponseLengthBytes = knownResponseLengthCount > 0 ? okKnownLengths[^1] : 0d;

        var okLatencies = requestData.Where(d => !d.IsError()).Select(d => d.ResponseTime.TotalMilliseconds).ToList();
        okLatencies.Sort();

        var durationSeconds = requestData.Count == 0
            ? 0d
            : requestData.Max(d => d.RequestTimeLine.TotalSeconds);
        if (durationSeconds <= 0d) durationSeconds = 0d;

        var averageRps = durationSeconds > 0 ? total / durationSeconds : 0d;

        var maxLatency = okLatencies.Count > 0 ? okLatencies[^1] : 0d;
        var meanLatency = okLatencies.Count > 0 ? okLatencies.Average() : 0d;

        var phaseBreakdown = requestData
            .Where(d => !string.IsNullOrWhiteSpace(d.PhaseName))
            .GroupBy(d => d.PhaseName!)
            .Select(g =>
            {
                var phaseData = g.ToList();
                var phaseErrors = phaseData.Count(d => d.IsError());
                var phaseOkLatencies = phaseData
                    .Where(d => !d.IsError())
                    .Select(d => d.ResponseTime.TotalMilliseconds)
                    .ToList();
                phaseOkLatencies.Sort();

                var phaseDuration = phaseData.Count > 0
                    ? phaseData.Max(d => d.RequestTimeLine.TotalSeconds) -
                      phaseData.Min(d => d.RequestTimeLine.TotalSeconds)
                    : 0d;
                if (phaseDuration < 0.1) phaseDuration = 1.0;

                return new
                {
                    Phase = g.Key,
                    Requests = phaseData.Count,
                    Errors = phaseErrors,
                    ErrorRatio = phaseData.Count > 0 ? (double)phaseErrors / phaseData.Count : 0d,
                    DurationSeconds = phaseDuration,
                    AverageRps = phaseDuration > 0 ? phaseData.Count / phaseDuration : 0d,
                    MeanLatencyMs = phaseOkLatencies.Count > 0 ? phaseOkLatencies.Average() : 0d,
                    P50LatencyMs = StatsUtils.Percentile(phaseOkLatencies, 0.50),
                    P95LatencyMs = StatsUtils.Percentile(phaseOkLatencies, 0.95),
                    P99LatencyMs = StatsUtils.Percentile(phaseOkLatencies, 0.99)
                };
            })
            .ToList();

        var summary = new
        {
            Endpoint = url,
            Requests = total,
            Ok = ok,
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
            MeanResponseLengthBytes = meanResponseLengthBytes,
            MaxResponseLengthBytes = maxResponseLengthBytes,
            DurationSeconds = durationSeconds,
            AverageRps = averageRps,
            MeanLatencyMs = meanLatency,
            MaxLatencyMs = maxLatency,
            P50LatencyMs = StatsUtils.Percentile(okLatencies, 0.50),
            P90LatencyMs = StatsUtils.Percentile(okLatencies, 0.90),
            P95LatencyMs = StatsUtils.Percentile(okLatencies, 0.95),
            P99LatencyMs = StatsUtils.Percentile(okLatencies, 0.99),
            PhaseBreakdown = phaseBreakdown
        };

        return JsonSerializer.Serialize(summary, Options);
    }
}
