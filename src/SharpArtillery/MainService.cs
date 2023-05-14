#pragma warning disable CA1848
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.Reporting;

namespace SharpArtillery;

public class MainService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainService> _logger;

    public MainService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainService>();
    }

    public async Task Main(string[] args)
    {
        var flags = ProgramSettings.HandleConfigsAndSettings(args);

        if (flags.Quit) return;

        var httpClientFactory = new CustomHttpClientFactory(null, FactoryEnum.Roger, flags);


        // do we have phases?
        Settings? settings;
        // Manager? manager = null;
        var manager = new Manager(httpClientFactory, _loggerFactory);
        
        if (flags.YamlConfig != null)
        {
            var config = flags.YamlConfig;
            foreach (var phase in config.Settings.Phases)
            {
                var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
                // TODO: to fully support phases, I think we need to change some more code...
                // https://www.artillery.io/docs/guides/guides/test-script-reference
                settings = new Settings
                {
                    Vu = phase.Vu,
                    Duration = phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : null,
                    Target = endpoint,
                    MaxRequests = phase.Requests > 0 ? phase.Requests : null
                };
                await RunPhaseTest(manager, settings);
            }
        }
        else
        {
            // when no yaml is used``
            settings = new Settings
            {
                JsonContent = flags.JsonContent,
                Vu = flags.Clients,
                Target = flags.Target,
                Method = flags.Method,
                Duration = flags.Duration,
                MaxRequests = flags.MaxRequests,
                Headers = flags.Headers,
                ConstantRps = flags.ConstantRps
            };
            await RunNonPhaseTest(manager, settings);
        }

        if (flags.ReportSettings.Enabled)
            await WriteReportAsync(flags.ReportSettings, manager!.Results,
                flags.YamlConfig != null
                    ? flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url
                    : flags.Target);
    }

    private async Task RunPhaseTest(Manager manager, Settings settings)
    {
        await manager.PrepareForTest(settings);
        var runningTest = new Thread(manager.RunTest) { IsBackground = true };
        runningTest.Start();
        
        // TODO: report relevant things about which phase
        // report progress from manager
        while (runningTest.IsAlive)
        {
            var progress = manager.GetProgress;
            // TODO: https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging
            Console.Out.WriteLine(
                $"Requests: {progress.Requests} ({progress.PercentDone}%), requests per second: {progress.Rps}, " +
                $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
            await Task.Delay(1000);
        }
        await manager.ProcessData();
        
        // TODO: to not clear out the results, we should enter results into some temporarily storage
        // adding this to a csv file would be best, which could be parsed at the end of the test
    }

    private async Task RunNonPhaseTest(Manager manager, Settings settings)
    {
        // var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
        await manager.PrepareForTest(settings);
        var runningTest = new Thread(manager.RunTest) { IsBackground = true };
        runningTest.Start();

        // report progress from manager
        while (runningTest.IsAlive)
        {
            var progress = manager.GetProgress;
            // TODO: https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging
            Console.Out.WriteLine(
                $"Requests: {progress.Requests} ({progress.PercentDone}%), requests per second: {progress.Rps}, " +
                $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
            await Task.Delay(1000);
        }

        await manager.ProcessData();

        Debug.Assert(manager.Results.Count == manager.GetProgress.Requests);


        StandardOutReport(manager.TotalTimeTimer, manager.Results);
        manager.Report();
    }


    // ReSharper disable once UnusedMember.Local
    private static async Task WriteReportAsync(ReportSettings reportSettings, List<DataPoint> responseData,
        string endpoint)
    {
        switch (reportSettings.Extension)
        {
            case ".xlsx":
                // TODO: enable excel reporting again
                throw new NotImplementedException();
            // MyExcel.Create(outputPath, requests);
            case ".html":
#pragma warning disable IL2026
                await HtmlReport.CreateAsync(reportSettings.Name + reportSettings.Extension, responseData, endpoint);
#pragma warning restore IL2026
                break;
            default:
                throw new NotImplementedException();
        }
    }

    private void StandardOutReport(Stopwatch timer, IReadOnlyCollection<DataPoint> completedTasks)
    {
        Console.Out.WriteLine($"Test took {timer.Elapsed.TotalSeconds} seconds");
        // aggregate results
        var averageLatency = completedTasks.Average(r => r.ResponseTime.TotalMilliseconds);
        var maxLatency = completedTasks.MaxBy(r => r.ResponseTime).ResponseTime.TotalMilliseconds;

        // breakdown of latency
        Console.Out.WriteLine($"Average latency: {averageLatency:0.00} ms");
        Console.Out.WriteLine($"Max     latency: {maxLatency} ms");

        // sum up all different statuscodes
        var countedStatuses = from d in completedTasks
            group d by d.Status
            into status
            select new { StatusCode = status, Count = status.Count() };
        foreach (var status in countedStatuses)
        {
            Console.Out.WriteLine($"{status.StatusCode.Key} : {status.Count}");
        }

        Console.Out.WriteLine($"Errors: {completedTasks.Count(x => !string.IsNullOrEmpty(x.Error))}");
        Console.Out.WriteLine(
            $"Error Text: {completedTasks.FirstOrDefault(x => !string.IsNullOrEmpty(x.Error)).Error}");
    }
}