using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SharpArtillery.Configs;
using SharpArtillery.Models;

namespace SharpArtillery;

/// <summary>
/// Formats and writes test results to the console.
/// Consolidates summary stats and percentile reporting.
/// </summary>
internal sealed class ConsoleReporter
{
    private readonly Settings _settings;
    private readonly List<DataPoint> _responseData;
    private readonly TimeSpan _totalElapsed;
    private readonly int _averageRps;
    private readonly (int BestClients, double BestRps)? _autoTuneResult;

    public ConsoleReporter(
        Settings settings,
        List<DataPoint> responseData,
        TimeSpan totalElapsed,
        int averageRps,
        (int BestClients, double BestRps)? autoTuneResult = null)
    {
        _settings = settings;
        _responseData = responseData;
        _totalElapsed = totalElapsed;
        _averageRps = averageRps;
        _autoTuneResult = autoTuneResult;
    }

    /// <summary>
    /// Prints basic summary: elapsed time, latency stats, status code breakdown.
    /// </summary>
    public void PrintSummary()
    {
        Console.Out.WriteLine($"Test took {_totalElapsed.TotalSeconds} seconds");

        if (_responseData.Count == 0)
        {
            Console.Out.WriteLine("No requests completed.");
            return;
        }

        var averageLatency = _responseData.Average(r => r.ResponseTime.TotalMilliseconds);
        var maxLatency = _responseData.MaxBy(r => r.ResponseTime).ResponseTime.TotalMilliseconds;

        Console.Out.WriteLine($"Average latency: {averageLatency:0.00} ms");
        Console.Out.WriteLine($"Max     latency: {maxLatency} ms");

        var countedStatuses = from d in _responseData
            group d by d.Status
            into status
            select new { StatusCode = status.Key, Count = status.Count() };
        foreach (var status in countedStatuses)
        {
            Console.Out.WriteLine($"{status.StatusCode} : {status.Count}");
        }

        var errors = _responseData.Count(IsError);
        Console.Out.WriteLine($"Errors: {errors}");
        var firstError = _responseData.FirstOrDefault(x => !string.IsNullOrEmpty(x.Error));
        var firstErrorText = string.IsNullOrEmpty(firstError.Error) ? null : firstError.Error;
        Console.Out.WriteLine($"Error Text: {firstErrorText}");
    }

    /// <summary>
    /// Prints detailed percentile breakdown for OK and error requests.
    /// </summary>
    public void PrintPercentiles()
    {
        var okRequests = _responseData.Where(x => !IsError(x)).ToList();
        var errorRequests = _responseData.Where(IsError).ToList();

        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Target URL:", _settings.Target));
        Console.Out.WriteLine(_settings.MaxRequests > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Max requests:", _settings.MaxRequests)
            : string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Duration:",
                _settings.Duration!.Value.ToString()));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Concurrency level:",
            _settings.Vu));
        Console.Out.WriteLine();

        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Completed requests:",
            okRequests.Count + errorRequests.Count));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total errors:",
            errorRequests.Count));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total time:",
            $"{_totalElapsed.TotalSeconds} s"));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Average RPS:",
            _averageRps));
        if (_autoTuneResult.HasValue)
        {
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}",
                "AutoTune peak (sampled):",
                $"{_autoTuneResult.Value.BestRps:F1} rps @ {_autoTuneResult.Value.BestClients} clients"));
        }
        var mean = okRequests.Count > 0 ? okRequests.Average(r => r.ResponseTime.TotalMilliseconds) : 0;
        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Mean latency:", $"{mean:F} ms"));
        Console.Out.WriteLine();

        PrintPercentileSection("Percentage of the OK requests served within a certain time", okRequests, "No requests went fine!");
        PrintPercentileSection("Percentage of the ERROR requests served within a certain time", errorRequests, "No Errors");
    }

    private static void PrintPercentileSection(string header, List<DataPoint> requests, string emptyMessage)
    {
        Console.Out.WriteLine(header);
        var latencies = requests.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        latencies.Sort();

        if (latencies.Count == 0)
        {
            Console.Out.WriteLine(emptyMessage);
            return;
        }

        PrintPercentile(latencies, 0.50f, "50%");
        PrintPercentile(latencies, 0.90f, "90%");
        PrintPercentile(latencies, 0.95f, "95%");
        PrintPercentile(latencies, 0.99f, "99%");
        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "100%", $"{latencies[^1]:F} ms"));
    }

    private static void PrintPercentile(List<double> sortedLatencies, float percent, string label)
    {
        var index = (int)MathF.Round(percent * sortedLatencies.Count);
        if (index >= sortedLatencies.Count)
            index = sortedLatencies.Count - 1;
        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", label, $"{sortedLatencies[index]:F} ms"));
    }

    /// <summary>
    /// Determines if a data point represents an error response.
    /// </summary>
    internal static bool IsError(DataPoint dataPoint) =>
        !string.IsNullOrEmpty(dataPoint.Error) || (int)dataPoint.Status < 200 || (int)dataPoint.Status >= 300;
}
