using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpArtillery;

/*
BUGS:
        
TODO:
    - fix the duration flag, as right now its not working
    - add flags for payload and type of request GET POST
 * FEATURE:
 *  - adjust the number of http connection to server in http factory, to the number of vu's
 *  - add version check
 * - Redirect console output? can it already be done? Or we should write to StdOut instead?
 * - a graph for each phase?
 * - fix colored text for console?
 */


var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<MainService>();
        services.AddLogging();

    })
    .ConfigureLogging(a =>
    {
        a.ClearProviders();
        a.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Debug);
    })
    .Build();


var program = host.Services.GetRequiredService<MainService>();
await program.Main(args);

// await host.RunAsync();


public struct DataPoint
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