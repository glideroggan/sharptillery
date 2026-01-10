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

public sealed class MainService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainService> _logger;

    public MainService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainService>();
    }

    /// <summary>
    /// Main entry point - receives pre-validated config from CommandLineParser
    /// </summary>
    public async Task Run(ArtilleryConfig flags)
    {
        var httpClientFactory = new CustomHttpClientFactory(null, FactoryEnum.Roger, flags);

        Settings? settings;
        var manager = new Manager(httpClientFactory, _loggerFactory);
        
        if (flags.YamlConfig != null)
        {
            var config = flags.YamlConfig;

            var baseTarget = config.Settings.Target
                             ?? throw new InvalidOperationException("YAML config is missing settings.target");
            var phases = config.Settings.Phases
                         ?? throw new InvalidOperationException("YAML config is missing settings.phases");
            var getUrl = config.Scenarios?
                             .FirstOrDefault()?
                             .Flow?
                             .FirstOrDefault()?
                             .Get?
                             .Url
                         ?? throw new InvalidOperationException(
                             "YAML config is missing scenarios[0].flow[0].get.url");

            var endpoint = baseTarget + getUrl;

            foreach (var phase in phases)
            {
                settings = flags.ToSettings(
                    targetOverride: endpoint,
                    vuOverride: phase.Vu,
                    durationOverride: phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : null,
                    maxRequestsOverride: phase.Requests > 0 ? phase.Requests : null);

                settings.Validate($"Phase '{phase.Name ?? "(unnamed)"}'");

                await RunPhaseTest(manager, settings);
            }

            if (flags.ReportSettings.Enabled)
                await WriteReportAsync(flags.ReportSettings, manager.Results, endpoint);
        }
        else
        {
            // Non-YAML mode - config already validated by CommandLineParser
            var target = flags.Target!;

            settings = flags.ToSettings(target);
            await RunNonPhaseTest(manager, settings);

            if (flags.ReportSettings.Enabled)
                await WriteReportAsync(flags.ReportSettings, manager.Results, target);
        }
    }

    private static async Task RunPhaseTest(Manager manager, Settings settings)
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

    private static async Task RunNonPhaseTest(Manager manager, Settings settings)
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

        var reporter = new ConsoleReporter(
            manager.Settings,
            manager.Results,
            manager.TotalTimeTimer.Elapsed,
            manager.AverageRps,
            manager.AutoTuneResult);
        reporter.PrintSummary();
        reporter.PrintPercentiles();
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
}