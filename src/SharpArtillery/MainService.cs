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
using SharpArtillery.YamlConfig;

namespace SharpArtillery;

public sealed class MainService
{
    private readonly ILoggerFactory _loggerFactory;

    public MainService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Main entry point - receives pre-validated config from CommandLineParser
    /// </summary>
    public async Task Run(ArtilleryConfig flags)
    {
        var httpClientFactory = new CustomHttpClientFactory(flags);

        Settings? settings;
        var manager = new Manager(httpClientFactory, _loggerFactory);
        
        if (flags.YamlConfig != null)
        {
            var config = flags.YamlConfig;

            var baseTarget = config.Settings.Target
                             ?? throw new InvalidOperationException("YAML config is missing settings.target");
            var phases = config.GetPhases()
                         ?? throw new InvalidOperationException("YAML config is missing phases");
            
            // Get flow steps from the first scenario
            var flowSteps = config.Scenarios?
                                .FirstOrDefault()?
                                .Flow;
            
            if (flowSteps == null || flowSteps.Count == 0)
                throw new InvalidOperationException(
                    "YAML config is missing flow steps in scenarios[0].flow");
            
            // Determine if we need flow mode (multi-step) or can use blast mode (single step, no captures)
            var useFlowMode = IsFlowModeRequired(flowSteps);
            
            if (useFlowMode)
            {
                // Flow mode: each VU executes the full flow sequence with state
                var requestTimeout = config.Settings.GetTimeout() != null
                    ? TimeSpan.FromSeconds(config.Settings.GetTimeout()!.Value)
                    : TimeSpan.FromSeconds(5);
                
                await RunFlowModeAsync(flowSteps, baseTarget, requestTimeout, phases, flags.ReportSettings);
            }
            else
            {
                // Blast mode: single-step flow, use shared request queue for max throughput
                var firstStep = flowSteps[0];
                var httpRequest = firstStep.GetHttpRequest();
                if (httpRequest == null)
                    throw new InvalidOperationException(
                        "YAML config flow step must be an HTTP request (not think) for blast mode");
                
                var (method, spec) = httpRequest.Value;
                var requestUrl = spec.Url
                             ?? throw new InvalidOperationException(
                                 "YAML config flow step is missing url");

                var endpoint = baseTarget.TrimEnd('/') + requestUrl;

                foreach (var phase in phases)
                {
                    settings = flags.ToSettings(
                        targetOverride: endpoint,
                        vuOverride: phase.Vu,
                        durationOverride: phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : null,
                        maxRequestsOverride: phase.Requests.HasValue && phase.Requests > 0 ? phase.Requests : null,
                        phaseName: phase.Name,
                        methodOverride: method,
                        headersOverride: spec.Headers,
                        jsonContentOverride: spec.Json);

                    settings.Validate($"Phase '{phase.Name ?? "(unnamed)"}'");

                    await RunPhaseTest(manager, settings, phase.Name);
                }

                if (flags.ReportSettings.Enabled)
                    await WriteReportAsync(flags.ReportSettings, manager.Results, endpoint);
            }
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
    
    /// <summary>
    /// Determines if flow mode is required based on flow complexity.
    /// Flow mode is needed when:
    /// - Multiple steps in the flow
    /// - Any step has captures (response extraction)
    /// - Any step is a think step
    /// </summary>
    private static bool IsFlowModeRequired(List<FlowStep> flowSteps)
    {
        // Multiple steps always requires flow mode
        if (flowSteps.Count > 1)
            return true;
        
        var step = flowSteps[0];
        
        // Think steps require flow mode
        if (step.IsThink)
            return true;
        
        // Steps with captures require flow mode
        var httpRequest = step.GetHttpRequest();
        if (httpRequest?.Spec.Capture != null && httpRequest.Value.Spec.Capture.Count > 0)
            return true;
        
        return false;
    }
    
    /// <summary>
    /// Runs flow mode where each VU executes the full flow sequence with state.
    /// </summary>
    private static async Task RunFlowModeAsync(
        List<FlowStep> flowSteps,
        string baseTarget,
        TimeSpan requestTimeout,
        List<Phase> phases,
        ReportSettings reportSettings)
    {
        Console.Out.WriteLine($"[Flow Mode] Detected {flowSteps.Count} flow steps - using per-VU flow execution");
        
        // Use a shared global timer for report timeline
        var globalTimer = new Stopwatch();
        globalTimer.Start();
        
        // Collect all results across phases
        var allResults = new List<DataPoint>();
        
        foreach (var phase in phases)
        {
            var phaseLabel = string.IsNullOrEmpty(phase.Name) ? "Phase" : $"Phase '{phase.Name}'";
            var duration = phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : (TimeSpan?)null;
            var maxRequestsPerVu = phase.Requests.HasValue && phase.Requests > 0
                ? phase.Requests.Value / Math.Max(phase.Vu, 1)
                : (int?)null;
            
            Console.Out.WriteLine($"\n=== {phaseLabel} started (VU: {phase.Vu}, Duration: {phase.Duration ?? 0}s, Requests: {phase.Requests ?? 0}) ===");
            
            using var runner = new FlowRunner(flowSteps, baseTarget, requestTimeout, globalTimer);
            runner.PhaseName = phase.Name;
            runner.Prepare(phase.Vu);
            runner.Start(maxRequestsPerVu, duration);
            
            var phaseStartTime = DateTime.UtcNow;
            var lastReportedRequests = 0L;
            
            // Report progress while running
            while (runner.IsRunning)
            {
                await Task.Delay(1000);
                
                var (requests, errors, activeVUs) = runner.GetProgress();
                var elapsed = DateTime.UtcNow - phaseStartTime;
                var rps = elapsed.TotalSeconds > 0 ? (requests - lastReportedRequests) : 0;
                var errorRatio = requests > 0 ? (double)errors / requests * 100 : 0;
                var percentDone = duration.HasValue
                    ? (int)(elapsed.TotalMilliseconds / duration.Value.TotalMilliseconds * 100)
                    : (phase.Requests.HasValue ? (int)(requests * 100 / phase.Requests.Value) : 0);
                
                Console.Out.WriteLine(
                    $"[{phaseLabel}] Requests: {requests} ({Math.Min(percentDone, 100)}%), RPS: {rps}, " +
                    $"Error ratio: {errorRatio:F}%, Active VUs: {activeVUs}");
                
                lastReportedRequests = requests;
            }
            
            await runner.WaitForCompletionAsync();
            
            // Drain results from the runner
            var phaseResults = new List<DataPoint>();
            while (runner.ResultQueue.TryDequeue(out var dataPoint))
            {
                phaseResults.Add(dataPoint);
                allResults.Add(dataPoint);
            }
            
            var phaseRps = phaseResults.Count > 0 
                ? (int)(phaseResults.Count / (DateTime.UtcNow - phaseStartTime).TotalSeconds)
                : 0;
            var phaseErrorCount = phaseResults.Count(dp => dp.IsError());
            var phaseErrorRatio = phaseResults.Count > 0 ? phaseErrorCount * 100.0 / phaseResults.Count : 0;
            
            Console.Out.WriteLine($"=== {phaseLabel} completed: {phaseResults.Count} requests, {phaseRps} avg RPS, {phaseErrorRatio:F}% errors ===\n");
        }
        
        globalTimer.Stop();
        
        // Calculate RPS for each data point (same algorithm as Manager.ProcessData)
        if (allResults.Count > 0)
        {
            allResults.Sort((a, b) =>
                a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
                a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

            var left = 0;
            for (var right = 0; right < allResults.Count; right++)
            {
                var point = allResults[right];
                var windowStart = point.RequestReceivedTime.AddSeconds(-1);

                while (left <= right && allResults[left].RequestReceivedTime <= windowStart)
                    left++;

                point.Rps = right - left + 1;
                allResults[right] = point;
            }
        }
        
        Console.Out.WriteLine("Processing data...");
        
        if (reportSettings.Enabled)
            await WriteReportAsync(reportSettings, allResults, baseTarget);
    }

    private static async Task RunPhaseTest(Manager manager, Settings settings, string? phaseName)
    {
        var phaseLabel = string.IsNullOrEmpty(phaseName) ? "Phase" : $"Phase '{phaseName}'";
        Console.Out.WriteLine($"\n=== {phaseLabel} started (VU: {settings.Vu}, Duration: {settings.Duration?.TotalSeconds ?? 0}s, Requests: {settings.MaxRequests ?? 0}) ===");
        
        await manager.PrepareForTest(settings);
        var runningTest = new Thread(manager.RunTest) { IsBackground = true };
        runningTest.Start();
        
        // report progress from manager
        while (runningTest.IsAlive)
        {
            var progress = manager.GetProgress;
            // TODO: https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging
            Console.Out.WriteLine(
                $"[{phaseLabel}] Requests: {progress.Requests} ({progress.PercentDone}%), RPS: {progress.Rps}, " +
                $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
            await Task.Delay(1000);
        }
        await manager.ProcessData();
        
        // Print phase summary
        var phaseRequests = manager.GetProgress.Requests;
        var phaseRps = manager.AverageRps;
        var phaseErrors = manager.GetProgress.ErrorRatio * 100;
        Console.Out.WriteLine($"=== {phaseLabel} completed: {phaseRequests} requests, {phaseRps} avg RPS, {phaseErrors:F}% errors ===\n");
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