using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpArtillery.Configs;
using SharpArtillery.Tests.Stubs;
using SharpArtillery.YamlConfig;
using Xunit;

namespace SharpArtillery.Tests;

/// <summary>
/// Whole YAML runs through <see cref="MainService"/>, with the stub in place of the network.
/// </summary>
public class YamlRunTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Runs <paramref name="run"/> with the console captured; test parallelization is off.</summary>
    private static async Task<string> CaptureConsoleAsync(Func<Task> run)
    {
        var saved = Console.Out;
        using var buffer = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(buffer));
        try
        {
            await run().WaitAsync(Patience);
        }
        finally
        {
            Console.SetOut(saved);
        }
        return buffer.ToString();
    }

    private static ArtilleryConfig YamlFlags(List<Phase> phases, List<FlowStep> flow) => new()
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        YamlConfig = new ConfigRoot
        {
            Settings = new ConfigSettings { Target = "http://stub.test" },
            Phases = phases,
            Scenarios = [new Scenario { Flow = flow }]
        }
    };

    // Red for slice 4: a YAML run printed only the per-phase lines, never the latency summary
    // that a flags run ends with.
    [Fact]
    [Trait("slice", "4")]
    public async Task A_blast_yaml_run_ends_with_the_latency_summary_of_every_phase()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        var flags = YamlFlags(
            [new Phase { Name = "a", Vu = 1, Requests = 3 }, new Phase { Name = "b", Vu = 2, Requests = 4 }],
            [new FlowStep { Get = new RequestSpec { Url = "/x" } }]);

        var output = await CaptureConsoleAsync(() =>
            new MainService(NullLoggerFactory.Instance).Run(flags, factory));

        Assert.Equal(7, stub.CallCount);
        Assert.Matches(@"Completed requests:\s+7", output);
        Assert.Contains("Mean latency:", output);
        Assert.Contains("Percentage of the OK requests served within a certain time", output);
    }

    [Fact]
    [Trait("slice", "4")]
    public async Task A_flow_yaml_run_ends_with_the_latency_summary()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        var flags = YamlFlags(
            [new Phase { Name = "flow", Vu = 2, Requests = 4 }],
            [new FlowStep { Get = new RequestSpec { Url = "/a" } }, new FlowStep { Get = new RequestSpec { Url = "/b" } }]);

        var output = await CaptureConsoleAsync(() =>
            new MainService(NullLoggerFactory.Instance).Run(flags, factory, flowHandler: stub));

        Assert.Equal(4, stub.CallCount);
        Assert.Matches(@"Completed requests:\s+4", output);
        Assert.Contains("Mean latency:", output);
        Assert.Contains("Percentage of the OK requests served within a certain time", output);
    }

    // Red for slice 4: the phase total was divided per VU with integer division, so 7 requests over
    // 3 VUs sent 6 (2 each) and 2 over 5 sent 5 (each VU still sent its first request).
    [Theory]
    [InlineData(3, 7)]
    [InlineData(5, 2)]
    [InlineData(4, 4)]
    [Trait("slice", "4")]
    public async Task Flow_requests_are_a_phase_total_whatever_the_vu_count(int vu, int requests)
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep>
        {
            new() { Get = new RequestSpec { Url = "/a" } },
            new() { Think = 0 },
            new() { Get = new RequestSpec { Url = "/b" } }
        };
        var phases = new List<Phase> { new() { Name = "split", Vu = vu, Requests = requests } };

        await MainService.RunFlowModeAsync(steps, "http://stub.test", TimeSpan.FromSeconds(5), phases,
            new Models.ReportSettings(), stub).WaitAsync(Patience);

        Assert.Equal(requests, stub.CallCount);
    }

    // Red for slice 4: the duration was only checked between steps, so a request that had not
    // answered kept the phase open until the request timeout (here five minutes).
    [Fact]
    [Trait("slice", "4")]
    public async Task A_phase_duration_stops_a_request_that_is_still_waiting()
    {
        var stub = StubHttpHandler.Hanging();
        var steps = new List<FlowStep> { new() { Get = new RequestSpec { Url = "/slow" } } };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromMinutes(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(2);
        runner.Start(null, TimeSpan.FromMilliseconds(200));
        await runner.WaitForCompletionAsync().WaitAsync(Patience);

        Assert.Equal(2, stub.CallCount);
        // A request cut by the phase end has no outcome, so it is neither a result nor an error.
        Assert.Equal(0, runner.TotalRequests);
        Assert.Equal(0, runner.TotalErrors);
        Assert.Empty(runner.ResultQueue);
        Assert.Equal(2, runner.CancelledAtPhaseEnd);
    }

    // Red for slice 4: a think step slept its full length even when the phase had ended.
    [Fact]
    [Trait("slice", "4")]
    public async Task A_phase_duration_stops_a_think_step()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep>
        {
            new() { Get = new RequestSpec { Url = "/a" } },
            new() { Think = 600 },
            new() { Get = new RequestSpec { Url = "/b" } }
        };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(1);
        runner.Start(null, TimeSpan.FromMilliseconds(200));
        await runner.WaitForCompletionAsync().WaitAsync(Patience);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal(1, runner.TotalRequests);
    }

    // Red for slice 4 (audit of slice 3, doubt 4): flow mode added step headers to the request
    // headers with TryAddWithoutValidation, which drops Content-* names without a word.
    [Fact]
    [Trait("slice", "4")]
    public async Task A_content_type_step_header_reaches_the_server()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep>
        {
            new()
            {
                Post = new RequestSpec
                {
                    Url = "/x",
                    Body = "<a/>",
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/xml",
                        ["X-Api-Key"] = "abc"
                    }
                }
            }
        };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(1);
        runner.Start(1);
        await runner.WaitForCompletionAsync().WaitAsync(Patience);

        var sent = Assert.Single(stub.Requests);
        Assert.Equal("application/xml", sent.Headers["Content-Type"]);
        Assert.Equal("abc", sent.Headers["X-Api-Key"]);
    }

    [Fact]
    [Trait("slice", "4")]
    public async Task A_content_header_replaces_the_json_content_type()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep>
        {
            new()
            {
                Post = new RequestSpec
                {
                    Url = "/x",
                    Json = new Dictionary<string, object> { ["a"] = "1" },
                    Headers = new Dictionary<string, string> { ["Content-Type"] = "application/vnd.test+json" }
                }
            }
        };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(1);
        runner.Start(1);
        await runner.WaitForCompletionAsync().WaitAsync(Patience);

        Assert.Equal("application/vnd.test+json", Assert.Single(stub.Requests).Headers["Content-Type"]);
    }

    [Fact]
    [Trait("slice", "4")]
    public async Task A_content_header_without_a_body_is_sent_on_an_empty_body()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep>
        {
            new()
            {
                Get = new RequestSpec
                {
                    Url = "/x",
                    Headers = new Dictionary<string, string> { ["Content-Language"] = "sv" }
                }
            }
        };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(1);
        runner.Start(1);
        await runner.WaitForCompletionAsync().WaitAsync(Patience);

        Assert.Equal("sv", Assert.Single(stub.Requests).Headers["Content-Language"]);
    }
}
