using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SharpArtillery.Reporting;
using SharpArtillery.YamlConfig;

namespace SharpArtillery
{
    /*
     * FEATURE:
     *  
     * - Redirect console output? can it already be done? Or we should write to StdOut instead?
     * - a graph for each phase?
     */
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var flags = ProgramSettings.HandleConfigsAndSettings(args);
            if (flags == null) return;

            var httpClientFactory = new CustomHttpClientFactory(null, FactoryEnum.Roger, flags);


            // do we have phases?
            Settings? settings;
            if (flags.YamlConfig != null)
            {
                var config = flags.YamlConfig;
                foreach (var phase in config.Settings.Phases)
                {
                    // TODO: yaml config don't support duration yet
                    var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
                    settings = new Settings
                    {
                        Vu = phase.Vu,
                        Target = endpoint,
                        MaxRequests = phase.Requests
                    };
                    await RunTest(settings, httpClientFactory);
                }

                return;
            }

            // when no yaml is used
            settings = new Settings
            {
                Vu = flags.Clients,
                Target = flags.Target,
                Duration = flags.Duration,
                MaxRequests = flags.MaxRequests,
                ConstantRps = flags.ConstantRps
            };
            var manager = await RunTest(settings, httpClientFactory);

            if (flags.Report.Enabled)
                await WriteReportAsync(flags.Report, manager.Results,
                    flags.YamlConfig != null
                        ? flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url
                        : flags.Target);
        }

        private static async Task<Manager> RunTest(Settings settings, ICustomHttpClientFactory httpClientFactory)
        {
            // set up manager that will keep track of global state
            // var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
            var manager = new Manager(settings);
            try
            {
                // set up the clients
                var clients = new List<Client>();
                for (var i = 0; i < settings.Vu; i++)
                {
                    var c = new Client(i, manager, httpClientFactory);
                    clients.Add(c);
                }

                // start test
                foreach (var client in clients)
                {
                    _ = client.RunAsync();
                }

                // report progress from manager
                while (!manager.ClientKillToken.IsCancellationRequested)
                {
                    var progress = manager.GetProgress;
                    Console.WriteLine(
                        $"Requests: {progress.Requests} ({progress.PercentDone}%), requests per second: {progress.Rps}, " +
                        $"Error ratio: {progress.ErrorRatio * 100:F}%, mean latency: {progress.MeanLatency:F} ms");
                    await Task.Delay(3000);
                }
            }
            finally
            {
                await manager.UntilDoneAsync();
            }

            return manager;
        }


        // ReSharper disable once UnusedMember.Local
        private static async Task WriteReportAsync(Report report, List<Data> responseData, string endpoint)
        {
            // FEATURE: add progress for writing in console, like function for progress
            // FEATURE: streaming writing? Under longer tests we should stream the results into a csv that we can
            // later turn into an excel
            // FEATURE: we should add more info to the report about
            // - what was the average latency on that endpoint
            switch (report.Extension)
            {
                case ".xlsx":
                    // TODO: enable excel reporting again
                    throw new NotImplementedException();
                    // MyExcel.Create(outputPath, requests);
                    break;
                case ".html":
#pragma warning disable IL2026
                    await HtmlReport.CreateAsync(report.Name + report.Extension, responseData, endpoint);
#pragma warning restore IL2026
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        // ReSharper disable once UnusedMember.Local
        static void StandardOutReport(Stopwatch timer, IReadOnlyCollection<Data> completedTasks)
        {
            Console.WriteLine($"Test took {timer.Elapsed.TotalSeconds} seconds");
            // aggregate results
            var averageLatency = completedTasks.Average(r => r.Latency.TotalMilliseconds);
            var maxLatency = completedTasks.MaxBy(r => r.Latency).Latency.TotalMilliseconds;

            // breakdown of latency
            Console.WriteLine($"Average latency: {averageLatency:0.00} ms");
            Console.WriteLine($"Max     latency: {maxLatency} ms");

            // sum up all different statuscodes
            var countedStatuses = from d in completedTasks
                group d by d.Status
                into status
                select new { StatusCode = status, Count = status.Count() };
            foreach (var status in countedStatuses)
            {
                Console.WriteLine($"{status.StatusCode.Key} : {status.Count}");
            }

            Console.WriteLine($"Errors: {completedTasks.Count(x => !string.IsNullOrEmpty(x.Error))}");
        }
    }


    public class ArtilleryConfig
    {
        public string? Yaml { get; set; }
        public string? Target { get; set; }
        public ConfigRoot? YamlConfig { get; set; }
        public TimeSpan? Duration { get; set; }
        public int? RequestRate { get; set; }

        public int MaxRequests { get; set; }
        public int Clients { get; set; }
        public Report Report { get; } = new();
        public int ConstantRps { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
    }

    public class Report
    {
        public bool Enabled { get; set; }
        public string Name { get; set; } = "report";
        public string Extension { get; set; } = ".html";
    }
}

internal struct Data
{
    internal DateTime RequestSentTime;

    /// <summary>
    /// When the response was processed
    /// </summary>
    internal TimeSpan RequestTimeLine;

    internal DateTime RequestReceivedTime;
    internal TimeSpan Latency;
    public string PhaseName;
    public string? Error { get; set; }
    public int ConcurrentRequests { get; set; }
    public int RequestRate { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public HttpStatusCode Status { get; set; }
    public int Rps { get; set; }
}