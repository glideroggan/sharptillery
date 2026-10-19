using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
    public Task Run(ArtilleryConfig flags) => Run(flags, new CustomHttpClientFactory(flags));

    /// <param name="flags">The parsed command line, with the YAML config when <c>-y</c> was given.</param>
    /// <param name="httpClientFactory">Where blast mode gets its client (tests pass a stub).</param>
    /// <param name="flowHandler">Handler shared by every flow-mode VU (tests pass a stub); null gives each VU its own.</param>
    internal async Task Run(ArtilleryConfig flags, ICustomHttpClientFactory httpClientFactory,
        HttpMessageHandler? flowHandler = null)
    {
        Settings? settings;
        var manager = new Manager(httpClientFactory, _loggerFactory);
        
        // A CSV report is written row by row while the run reads its results. The file is opened here,
        // before the first request, so a path that cannot be written fails before anything is sent.
        using var csv = OpenCsvReport(flags.ReportSettings);
        manager.Csv = csv;

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
                
                await RunFlowModeAsync(flowSteps, baseTarget, requestTimeout, phases, flags.ReportSettings,
                    flowHandler, csv);
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

                var cancelledAtPhaseEnd = 0L;
                foreach (var phase in phases)
                {
                    settings = BlastPhaseSettings(flags, config, phase, endpoint, method, spec);

                    settings.Validate($"Phase '{phase.Name ?? "(unnamed)"}'");

                    cancelledAtPhaseEnd += await RunPhaseTest(manager, settings, phase.Name);
                }

                PrintRunSummary(YamlSummarySettings(endpoint, phases), manager.Results,
                    manager.GlobalTimeTimer.Elapsed, cancelledAtPhaseEnd);

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

        // Closing after the summary, so a CSV write that failed during the run is reported last.
        csv?.Close();
    }

    /// <summary>
    /// The CSV report file, opened before the run, or null when the run writes no CSV report.
    /// </summary>
    private static CsvReport? OpenCsvReport(ReportSettings reportSettings) =>
        reportSettings.Enabled && reportSettings.Extension == ".csv"
            ? CsvReport.Open(reportSettings.Name + reportSettings.Extension)
            : null;
    
    /// <summary>
    /// The settings of one blast-mode phase. The YAML <c>settings.timeout</c> replaces the
    /// <c>-T</c> timeout when it is set, and the step's <c>body</c> is sent when it has no <c>json</c>.
    /// </summary>
    internal static Settings BlastPhaseSettings(
        ArtilleryConfig flags,
        ConfigRoot config,
        Phase phase,
        string endpoint,
        string method,
        RequestSpec spec)
    {
        var settings = flags.ToSettings(
            targetOverride: endpoint,
            vuOverride: phase.Vu,
            durationOverride: phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : null,
            maxRequestsOverride: phase.Requests.HasValue && phase.Requests > 0 ? phase.Requests : null,
            phaseName: phase.Name,
            methodOverride: method,
            headersOverride: spec.Headers,
            jsonContentOverride: spec.Json,
            rawBodyOverride: spec.Body);

        if (config.Settings.GetTimeout() is { } timeoutSeconds)
            settings.RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        return settings;
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
    /// <param name="handler">Handler shared by every VU (tests pass a stub); null gives each VU its own.</param>
    internal static async Task RunFlowModeAsync(
        List<FlowStep> flowSteps,
        string baseTarget,
        TimeSpan requestTimeout,
        List<Phase> phases,
        ReportSettings reportSettings,
        HttpMessageHandler? handler = null,
        CsvReport? csv = null)
    {
        // csv, when given, is written row by row as results are drained; the caller closes it.
        Console.Out.WriteLine($"[Flow Mode] Detected {flowSteps.Count} flow steps - using per-VU flow execution");
        
        // Use a shared global timer for report timeline
        var globalTimer = new Stopwatch();
        globalTimer.Start();
        
        // Collect all results across phases
        var allResults = new List<DataPoint>();
        var cancelledAtPhaseEnd = 0L;

        foreach (var phase in phases)
        {
            var phaseLabel = string.IsNullOrEmpty(phase.Name) ? "Phase" : $"Phase '{phase.Name}'";
            var duration = phase.Duration.HasValue ? TimeSpan.FromSeconds(phase.Duration.Value) : (TimeSpan?)null;
            // The phase's requests are a total, shared by its VUs as blast mode shares -n.
            var maxRequests = phase.Requests is > 0 ? phase.Requests : null;

            Console.Out.WriteLine($"\n=== {phaseLabel} started (VU: {phase.Vu}, Duration: {phase.Duration ?? 0}s, Requests: {phase.Requests ?? 0}) ===");

            using var runner = new FlowRunner(flowSteps, baseTarget, requestTimeout, globalTimer, handler: handler);
            runner.PhaseName = phase.Name;
            runner.Prepare(phase.Vu);
            runner.Start(maxRequests, duration);
            
            var phaseStartTime = DateTime.UtcNow;
            var lastReportedRequests = 0L;
            var phaseResults = new List<DataPoint>();
            
            // Report progress while running
            while (runner.IsRunning)
            {
                await Task.Delay(1000);
                
                // Results are written as they come in, once a second, so the CSV report fills during the phase.
                DrainResults(runner, phaseResults, allResults, csv);

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
            
            // Drain what the last second did not reach
            DrainResults(runner, phaseResults, allResults, csv);
            
            var phaseRps = StatsUtils.AverageRps(phaseResults.Count, DateTime.UtcNow - phaseStartTime);
            var phaseErrorCount = phaseResults.Count(dp => dp.IsError());
            var phaseErrorRatio = phaseResults.Count > 0 ? phaseErrorCount * 100.0 / phaseResults.Count : 0;
            var phaseCancelled = runner.CancelledAtPhaseEnd;
            cancelledAtPhaseEnd += phaseCancelled;

            Console.Out.WriteLine($"=== {phaseLabel} completed: {phaseResults.Count} requests, {phaseRps} avg RPS, {phaseErrorRatio:F}% errors, {phaseCancelled} cancelled at phase end ===\n");
        }

        globalTimer.Stop();

        Console.Out.WriteLine("Processing data...");
        StatsUtils.SortByTimeline(allResults);

        PrintRunSummary(YamlSummarySettings(baseTarget, phases), allResults, globalTimer.Elapsed,
            cancelledAtPhaseEnd);

        if (reportSettings.Enabled)
            await WriteReportAsync(reportSettings, allResults, baseTarget);
    }

    /// <summary>
    /// Moves the results a flow runner has queued into the phase's list and the run's list, and writes
    /// them to the CSV report when there is one.
    /// </summary>
    private static void DrainResults(FlowRunner runner, List<DataPoint> phaseResults, List<DataPoint> allResults,
        CsvReport? csv)
    {
        var drained = false;
        while (runner.ResultQueue.TryDequeue(out var dataPoint))
        {
            phaseResults.Add(dataPoint);
            allResults.Add(dataPoint);
            csv?.Write(dataPoint);
            drained = true;
        }

        if (drained) csv?.Flush();
    }

    /// <summary>
    /// Prints the latency summary a flags run ends with, over every result of a YAML run.
    /// </summary>
    private static void PrintRunSummary(Settings summarySettings, List<DataPoint> results, TimeSpan elapsed,
        long cancelledAtPhaseEnd)
    {
        var reporter = new ConsoleReporter(summarySettings, results, elapsed,
            StatsUtils.AverageRps(results.Count, elapsed));
        reporter.PrintSummary();
        Console.Out.WriteLine($"Cancelled at phase end: {cancelledAtPhaseEnd}");
        reporter.PrintPercentiles();
    }

    /// <summary>
    /// What the summary says about a YAML run as a whole: the largest phase's users, and the
    /// phases' requests or durations added up when every phase uses the same one.
    /// </summary>
    internal static Settings YamlSummarySettings(string target, List<Phase> phases) => new()
    {
        Target = target,
        Vu = phases.Count > 0 ? phases.Max(p => p.Vu) : 0,
        MaxRequests = phases.Count > 0 && phases.All(p => p.Requests is > 0)
            ? phases.Sum(p => p.Requests!.Value)
            : null,
        Duration = phases.Count > 0 && phases.All(p => p.Duration.HasValue)
            ? TimeSpan.FromSeconds(phases.Sum(p => p.Duration!.Value))
            : null
    };

    /// <returns>Requests still in flight when the phase ended.</returns>
    private static async Task<int> RunPhaseTest(Manager manager, Settings settings, string? phaseName)
    {
        var phaseLabel = string.IsNullOrEmpty(phaseName) ? "Phase" : $"Phase '{phaseName}'";
        Console.Out.WriteLine($"\n=== {phaseLabel} started (VU: {settings.Vu}, Duration: {settings.Duration?.TotalSeconds ?? 0}s, Requests: {settings.MaxRequests ?? 0}) ===");
        
        await manager.PrepareForTest(settings);
        var runningTest = manager.RunTest();

        // report progress from manager
        while (!runningTest.IsCompleted)
        {
            var progress = manager.GetProgress;
            // TODO: https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging
            Console.Out.WriteLine(
                $"[{phaseLabel}] Requests: {progress.Requests} ({progress.PercentDone}%), RPS: {progress.Rps}, " +
                $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
            await Task.WhenAny(runningTest, Task.Delay(1000));
        }
        await runningTest;
        await manager.ProcessData();

        // Print phase summary
        var phaseRequests = manager.GetProgress.Requests;
        var phaseRps = manager.AverageRps;
        var phaseErrors = manager.GetProgress.ErrorRatio * 100;
        var phaseCancelled = manager.GetProgress.CancelledAtPhaseEnd;
        Console.Out.WriteLine($"=== {phaseLabel} completed: {phaseRequests} requests, {phaseRps} avg RPS, {phaseErrors:F}% errors, {phaseCancelled} cancelled at phase end ===\n");
        return phaseCancelled;
    }

    private static async Task RunNonPhaseTest(Manager manager, Settings settings)
    {
        // var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
        await manager.PrepareForTest(settings);
        var runningTest = manager.RunTest();

        // report progress from manager
        while (!runningTest.IsCompleted)
        {
            var progress = manager.GetProgress;
            // TODO: https://learn.microsoft.com/en-us/dotnet/core/extensions/high-performance-logging
            Console.Out.WriteLine(
                $"Requests: {progress.Requests} ({progress.PercentDone}%), requests per second: {progress.Rps}, " +
                $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
            await Task.WhenAny(runningTest, Task.Delay(1000));
        }
        await runningTest;

        await manager.ProcessData();

        Debug.Assert(manager.Results.Count == manager.GetProgress.Requests);

        var reporter = new ConsoleReporter(
            manager.Settings,
            manager.Results,
            manager.TotalTimeTimer.Elapsed,
            manager.AverageRps,
            manager.AutoTuneResult);
        reporter.PrintSummary();
        Console.Out.WriteLine($"Cancelled at phase end: {manager.GetProgress.CancelledAtPhaseEnd}");
        reporter.PrintPercentiles();
    }


    // ReSharper disable once UnusedMember.Local
    private static async Task WriteReportAsync(ReportSettings reportSettings, List<DataPoint> responseData,
        string endpoint)
    {
        switch (reportSettings.Extension)
        {
            // The CSV rows were written during the run, and the file is closed by Run.
            case ".csv":
                break;
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