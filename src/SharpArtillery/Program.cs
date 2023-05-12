using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpArtillery;

/*
BUGS:
        
TODO:
    ConstantRps is not implemented yet, after the change of flow
 FEATURE:
    - Able to choose reporting in samples, meaning that we get samples of the request data
        in intervals and create a report from that. So for example I would like to have points
        in the graphs every second and get an average of rps, latency and so on.
    - report in csv format?
    - Add data table of of the requests from the main graph
 *  - Make the ssl verification configurable through flags, like curl
 *  - make the timeout of the request in the clients configurable, through flags?
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