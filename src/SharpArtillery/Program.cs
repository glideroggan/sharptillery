using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpArtillery;

/*
BUGS:
    - not adding a value after a flag, like -c -n 100
        we should handle the "-c" case better, don't crash
    - even though report property is in the yaml, the report flag isn't set        
TODOS:
    Remove the possibility to run it just running it without any parameters. Its more usual to bring up options at
        this point
    ConstantRps is not implemented yet, after the change of flow
 FEATURES:
    - Request mode ✅
        complete the restructuring of the api to handle this mode
    - Scenario mode
        complete the restructuring of the api to handle this mode
        - a new flow for the manager that now needs to create new clients (or reuse) every second
            We don't want to create all the users in the same millisecond, then they will start their requests
            in the same time. The point of this mode is instead to measure the amount of users per second, even
            though we will also get the number of requests per second
        - complete phase "arrival rate"
        - complete phase "maxVusers"
        - complete phase "rampTo"
        - complete phase "arrivalCount"
        - make it so that we can do more than one phase in one test
    - Copy out version coming into description from external data, so it is easier to automate updates
    - Able to choose reporting in samples, meaning that we get samples of the request data
        in intervals and create a report from that. So for example I would like to have points
        in the graphs every second and get an average of rps, latency and so on.
    - report in csv format?
    - Add data table of of the requests from the main graph
 *  - Make the ssl verification configurable through flags, like curl
 *  - make the timeout of the request in the clients configurable, through flags?
 *  - adjust the number of http connection to server in http factory, to the number of vu's
 *  - add version flag ✅
 * - a graph for each phase?
 * - fix colored text for console?
 *
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