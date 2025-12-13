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
        // reportData.Sort((a, b) =>
        //     a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
        //     a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

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
        content = content.Replace("<# ENDPOINT #>", url);

        // add settings
        // var settingsJson = JsonSerializer.Serialize(settings, Options);
        // content = content.Replace("<# PUT SETTINGS HERE #>", settingsJson);
        
        // prepare data for html
        var seriesJson = PrepareTimeSeriesData(reportData);
        content = content.Replace("<# PUT DATA HERE #>", seriesJson);

        var summaryJson = PrepareSummaryData(reportData, url);
        content = content.Replace("<# PUT SUMMARY HERE #>", summaryJson);

        // write content back and we should be done
        await using var reportFile = File.OpenWrite(dest.Name);
        var byteArr = Encoding.UTF8.GetBytes(content);
        await reportFile.WriteAsync(byteArr);

        Console.Out.WriteLine("Report saved");
    }

    [RequiresUnreferencedCode(
        "Calls System.Text.Json.JsonSerializer.Serialize<System.Collections.Generic.IEnumerable<<anonymous type: int Time, System.TimeSpan Latency, int RequestRate, string Phase>>>(System.Collections.Generic.IEnumerable<<anonymous type: int Time, System.TimeSpan Latency, int RequestRate, string Phase>>, System.Text.Json.JsonSerializerOptions?)")]
    private static bool IsError(DataPoint dataPoint) =>
        !string.IsNullOrEmpty(dataPoint.Error) || (int)dataPoint.Status < 200 || (int)dataPoint.Status >= 300;

    private static string PrepareTimeSeriesData(List<DataPoint> requestData)
    {
        // Bucket by whole second.
        var buckets = requestData
            .GroupBy(d => (int)Math.Floor(d.RequestTimeLine.TotalSeconds))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var total = g.Count();
                var errors = g.Count(IsError);
                var okLatencies = g.Where(d => !IsError(d)).Select(d => d.ResponseTime.TotalMilliseconds).ToList();
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

    private static string PrepareSummaryData(List<DataPoint> requestData, string url)
    {
        var total = requestData.Count;
        var errors = requestData.Count(IsError);
        var ok = total - errors;

        var okLatencies = requestData.Where(d => !IsError(d)).Select(d => d.ResponseTime.TotalMilliseconds).ToList();
        okLatencies.Sort();

        static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        var durationSeconds = requestData.Count == 0
            ? 0d
            : requestData.Max(d => d.RequestTimeLine.TotalSeconds);
        if (durationSeconds <= 0d) durationSeconds = 0d;

        var averageRps = durationSeconds > 0 ? total / durationSeconds : 0d;

        var maxLatency = okLatencies.Count > 0 ? okLatencies[^1] : 0d;
        var meanLatency = okLatencies.Count > 0 ? okLatencies.Average() : 0d;

        var summary = new
        {
            Endpoint = url,
            Requests = total,
            Ok = ok,
            Errors = errors,
            ErrorRatio = total > 0 ? (double)errors / total : 0d,
            DurationSeconds = durationSeconds,
            AverageRps = averageRps,
            MeanLatencyMs = meanLatency,
            MaxLatencyMs = maxLatency,
            P50LatencyMs = Percentile(okLatencies, 0.50),
            P90LatencyMs = Percentile(okLatencies, 0.90),
            P95LatencyMs = Percentile(okLatencies, 0.95),
            P99LatencyMs = Percentile(okLatencies, 0.99)
        };

        return JsonSerializer.Serialize(summary, Options);
    }
}