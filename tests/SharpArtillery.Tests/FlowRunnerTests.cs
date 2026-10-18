using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SharpArtillery.Models;
using SharpArtillery.Tests.Stubs;
using SharpArtillery.YamlConfig;
using Xunit;

namespace SharpArtillery.Tests;

public class FlowRunnerTests
{
    [Fact]
    public async Task A_captured_value_is_sent_in_the_next_step()
    {
        var stub = new StubHttpHandler((req, _) => Task.FromResult(
            req.RequestUri!.AbsolutePath == "/login"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"abc"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK)));
        var steps = new List<FlowStep>
        {
            new()
            {
                Post = new RequestSpec
                {
                    Url = "/login",
                    Capture = [new CaptureSpec { Json = "$.token", As = "token" }]
                }
            },
            new()
            {
                Get = new RequestSpec
                {
                    Url = "/me",
                    Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer {{ token }}" }
                }
            }
        };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(1);
        runner.Start(maxRequestsPerVu: 2);
        await runner.WaitForCompletionAsync();

        var sent = stub.Requests.ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(HttpMethod.Post, sent[0].Method);
        Assert.Equal("http://stub.test/login", sent[0].Uri!.ToString());
        Assert.Equal("Bearer abc", sent[1].Headers["Authorization"]);
        Assert.Equal(2, runner.TotalRequests);
        Assert.Equal(0, runner.TotalErrors);
        Assert.Equal(2, runner.ResultQueue.Count);
    }

    [Fact]
    public async Task Every_vu_shares_the_injected_handler()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep> { new() { Get = new RequestSpec { Url = "/x" } } };

        using var runner = new FlowRunner(steps, "http://stub.test", TimeSpan.FromSeconds(5), new Stopwatch(),
            handler: stub);
        runner.Prepare(3);
        runner.Start(maxRequestsPerVu: 2);
        await runner.WaitForCompletionAsync();

        Assert.Equal(6, stub.CallCount);
        Assert.Equal(6, runner.TotalRequests);
    }

    // Red for slice 4: a phase's `requests` is a total for the phase. Today flow mode divides it
    // by the VU count with integer division (2 / 5 = 0) and each VU still sends one request,
    // so `requests: 2, vu: 5` sends 5.
    [Fact(Skip = "slice 4")]
    [Trait("slice", "4")]
    public async Task Yaml_requests_are_a_total_split_across_the_vus()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        var steps = new List<FlowStep> { new() { Get = new RequestSpec { Url = "/x" } } };
        var phases = new List<Phase> { new() { Name = "split", Vu = 5, Requests = 2 } };

        await MainService.RunFlowModeAsync(steps, "http://stub.test", TimeSpan.FromSeconds(5), phases,
            new ReportSettings(), stub);

        Assert.Equal(2, stub.CallCount);
    }
}
