using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpArtillery;

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