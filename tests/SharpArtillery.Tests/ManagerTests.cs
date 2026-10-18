using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.Tests.Stubs;
using SharpArtillery.YamlConfig;
using Xunit;

namespace SharpArtillery.Tests;

public class ManagerTests
{
    private const string Target = "http://stub.test/x";

    // Every wait in these tests is bounded by this; a run that takes longer is a hang, not slowness.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static async Task RunPhaseAsync(Manager manager, Settings settings)
    {
        await manager.PrepareForTest(settings);
        await manager.RunTest().WaitAsync(Patience);
        await manager.ProcessData();
    }

    /// <summary>Polls until <paramref name="condition"/> holds; the signal has no awaitable form.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting until {what}");
            await Task.Delay(1);
        }
    }

    [Fact]
    public async Task Max_requests_sends_exactly_that_many()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 2, MaxRequests = 10 });

        Assert.Equal(10, stub.CallCount);
        Assert.Equal(10, manager.Results.Count);
        Assert.All(manager.Results, d => Assert.Equal(HttpStatusCode.OK, d.Status));
    }

    // The -n counter is shared by every client: a count that does not divide evenly over the
    // clients still sends exactly that many, and a failed request uses up its slot like any other.
    [Fact]
    public async Task Max_requests_is_shared_by_all_clients_and_counts_failures()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 7, MaxRequests = 25 });

        Assert.Equal(25, stub.CallCount);
        Assert.Equal(25, manager.Results.Count);

        var failing = new StubHttpHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var failingFactory = new StubHttpClientFactory(failing);
        using var failingManager = new Manager(failingFactory, NullLoggerFactory.Instance);

        await RunPhaseAsync(failingManager, new Settings { Target = Target, Vu = 3, MaxRequests = 9 });

        Assert.Equal(9, failing.CallCount);
        Assert.Equal(9, failingManager.Results.Count);
        Assert.All(failingManager.Results, d => Assert.Equal("http_request_exception", d.ErrorType));
        Assert.Equal(1f, failingManager.GetProgress.ErrorRatio);
    }

    // No client sends before RunTest opens the gate, and every client is released by it.
    [Fact]
    public async Task Clients_wait_at_the_start_gate_until_RunTest()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await manager.PrepareForTest(new Settings { Target = Target, Vu = 3, MaxRequests = 3 });
        // There is no signal for "every client is parked on the gate", so give them time to run:
        // a late client can only make this check weaker, never fail it.
        await Task.Delay(100);

        Assert.False(manager.Started.IsCompleted);
        Assert.Equal(0, stub.CallCount);

        await manager.RunTest().WaitAsync(Patience);

        Assert.True(manager.Started.IsCompleted);
        Assert.Equal(3, stub.CallCount);
        Assert.Equal(3, manager.Results.Count);
    }

    // A request still in flight when the phase ends is cancelled. Before slice 2 the client's
    // catch-all recorded it as a 500 (58 fake errors with -c 256), and when it was enqueued after
    // the run loop had stopped it landed in the next phase (phase B showed phase A's in-flight
    // requests as 100% errors). It is dropped and counted as cancelled at phase end instead.
    [Fact]
    public async Task A_request_cut_off_at_phase_end_is_not_a_500_and_does_not_leak_into_the_next_phase()
    {
        var hang = true;
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            if (!Volatile.Read(ref hang)) return new HttpResponseMessage(HttpStatusCode.OK);
            if (Interlocked.Increment(ref arrived) == 4) inFlight.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var factory = new StubHttpClientFactory(stub);
        var clock = new FakeTimeProvider();
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        await manager.PrepareForTest(new Settings
        {
            Target = Target,
            Vu = 4,
            Duration = TimeSpan.FromSeconds(1),
            RequestTimeout = TimeSpan.FromSeconds(30),
            PhaseName = "A"
        });
        var phaseA = manager.RunTest();
        await inFlight.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromSeconds(1));
        await phaseA.WaitAsync(Patience);

        Assert.Equal(4, stub.CallCount);
        Assert.Empty(manager.Results);
        Assert.Equal(4, manager.GetProgress.CancelledAtPhaseEnd);
        Assert.Equal(0, manager.GetProgress.Requests);
        Assert.Equal(0f, manager.GetProgress.ErrorRatio);

        Volatile.Write(ref hang, false);
        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 2, MaxRequests = 5, PhaseName = "B" });

        Assert.Equal(5, manager.Results.Count);
        Assert.All(manager.Results, d => Assert.Equal("B", d.PhaseName));
        Assert.All(manager.Results, d => Assert.Equal(HttpStatusCode.OK, d.Status));
        Assert.Equal(0, manager.GetProgress.CancelledAtPhaseEnd);
        Assert.Equal(0f, manager.GetProgress.ErrorRatio);
    }

    // Before slice 2 the phase's "% errors" was the error count of the last one-second interval
    // divided by every request of the phase. Here the first 10 requests fail, a progress tick
    // passes, then 10 succeed: the ratio must be 10/20, not 0/20.
    [Fact]
    public async Task The_phase_error_ratio_counts_the_whole_phase()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call <= 10) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var factory = new StubHttpClientFactory(stub);
        var clock = new FakeTimeProvider();
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        await manager.PrepareForTest(new Settings { Target = Target, Vu = 1, MaxRequests = 20 });
        var run = manager.RunTest();

        // The 11th request has arrived, so the 10 failures are written; a tick publishes them.
        await WaitUntilAsync(() => stub.CallCount == 11, "the 11th request arrives");
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => manager.GetProgress.Requests == 10, "the first progress line is published");
        Assert.Equal(1f, manager.GetProgress.ErrorRatio);

        release.SetResult();
        await run.WaitAsync(Patience);

        Assert.Equal(20, manager.Results.Count);
        Assert.Equal(10, manager.Results.Count(d => d.IsError()));
        Assert.Equal(20, manager.GetProgress.Requests);
        Assert.Equal(0.5f, manager.GetProgress.ErrorRatio);
        Assert.Equal(100, manager.GetProgress.PercentDone);
    }

    // Every result a client wrote before the phase ended is in Results after RunTest returns:
    // the channel is completed only once all clients have stopped, then drained.
    [Fact]
    public async Task Every_result_written_before_the_phase_end_is_drained()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        var clock = new FakeTimeProvider();
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        await manager.PrepareForTest(new Settings
        {
            Target = Target,
            Vu = 8,
            Duration = TimeSpan.FromSeconds(1)
        });
        var run = manager.RunTest();
        await WaitUntilAsync(() => stub.CallCount >= 2000, "2000 requests have been sent");
        clock.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(Patience);

        // The stub answers at once, so no request was cut off: every call has a result.
        Assert.Equal(0, manager.GetProgress.CancelledAtPhaseEnd);
        Assert.Equal(stub.CallCount, manager.Results.Count);
        Assert.Equal(stub.CallCount, manager.GetProgress.Requests);
        Assert.Equal(100, manager.GetProgress.PercentDone);
    }

    // "% done" and the request count are per phase: a second phase starts at 0 while Results
    // keep accumulating for the report.
    [Fact]
    public async Task Progress_counts_per_phase_while_results_accumulate()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 2, MaxRequests = 10, PhaseName = "A" });
        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 2, MaxRequests = 10, PhaseName = "B" });

        Assert.Equal(20, manager.Results.Count);
        Assert.Equal(10, manager.GetProgress.Requests);
        Assert.Equal(100, manager.GetProgress.PercentDone);
    }

    // Latency comes from one Stopwatch reading, and the DateTimes are derived from it, so start,
    // end and latency agree (BUG-003: two DateTime.UtcNow reads per request).
    [Fact]
    public async Task Latency_is_measured_with_the_stopwatch_and_the_times_agree()
    {
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings { Target = Target, Vu = 1, MaxRequests = 3 });

        Assert.Equal(3, manager.Results.Count);
        Assert.All(manager.Results, d =>
        {
            // Task.Delay can fire up to a timer tick early; it never comes close to 50 ms early.
            Assert.InRange(d.ResponseTime, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
            Assert.Equal(d.ResponseTime, d.Latency);
            Assert.Equal(d.StartTime + d.ResponseTime, d.EndTime);
            Assert.Equal(d.EndTime, d.RequestReceivedTime);
            Assert.Equal(d.StartTime, d.RequestSentTime);
            Assert.Equal(manager.GlobalStartUtc + d.RequestTimeLine, d.EndTime);
        });
        var timeline = manager.Results.Select(d => d.RequestTimeLine).ToList();
        Assert.Equal(timeline.OrderBy(t => t), timeline);
    }

    // The template is built per request, right before the send: a bad header fails at
    // PrepareForTest, as the pre-built queue did, not once per request during the run.
    [Fact]
    public async Task A_bad_header_fails_at_prepare_not_per_request()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<FormatException>(async () => await manager.PrepareForTest(new Settings
        {
            Target = Target,
            Vu = 1,
            MaxRequests = 1,
            Headers = new Dictionary<string, string> { ["X Bad Name"] = "x" }
        }));

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task Each_request_carries_the_phase_method_headers_and_body()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings
        {
            Target = Target,
            Vu = 2,
            MaxRequests = 4,
            Method = "POST",
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "abc" },
            JsonContent = new { a = 1 }
        });

        var sent = stub.Requests.ToList();
        Assert.Equal(4, sent.Count);
        Assert.All(sent, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal(Target, r.Uri!.ToString());
            Assert.Equal("abc", r.Headers["X-Api-Key"]);
            Assert.Equal("application/json", r.Headers["Content-Type"]);
        });
        // The 4 requests are 4 separate messages: the client disposes each one after sending it.
        Assert.Equal(4, stub.CallCount);
    }

    // `-H Content-Type:...` used to throw "Misused header name" while the request was built,
    // because Content-* headers belong on the content, not the request.
    [Fact]
    [Trait("slice", "3")]
    public async Task A_content_type_header_does_not_crash_the_run()
    {
        var stub = StubHttpHandler.Returning(HttpStatusCode.OK);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings
        {
            Target = Target,
            Vu = 1,
            MaxRequests = 1,
            Method = "POST",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            JsonContent = new { a = 1 }
        });

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    [Trait("slice", "3")]
    public void Content_headers_go_on_the_content_and_the_rest_on_the_request()
    {
        var template = new RequestTemplate(new Settings
        {
            Target = Target,
            Method = "POST",
            Headers = new Dictionary<string, string>
            {
                ["X-Api-Key"] = "abc",
                ["Content-Type"] = "application/vnd.test+json"
            },
            JsonContent = new { a = 1 }
        });

        using var req = template.Build();

        Assert.Equal("abc", req.Headers.GetValues("X-Api-Key").Single());
        // Contains() throws for a content header name on request headers, so enumerate instead.
        Assert.DoesNotContain(req.Headers, h => h.Key == "Content-Type");
        Assert.Equal("application/vnd.test+json", req.Content!.Headers.ContentType!.ToString());
    }

    // A content header with no body still reaches the server, on an empty body.
    [Fact]
    [Trait("slice", "3")]
    public void A_content_header_without_a_body_is_sent_on_an_empty_body()
    {
        var template = new RequestTemplate(new Settings
        {
            Target = Target,
            Method = "GET",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" }
        });

        using var req = template.Build();

        Assert.NotNull(req.Content);
        Assert.Equal("text/plain", req.Content!.Headers.ContentType!.MediaType);
    }
}

public class CustomHttpClientFactoryTests
{
    // The factory used to put every -H header on DefaultRequestHeaders, and Content-Type threw
    // "Misused header name" there, so the tool crashed at start.
    [Fact]
    [Trait("slice", "3")]
    public void A_content_type_header_does_not_crash_the_factory()
    {
        var config = new ArtilleryConfig
        {
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };

        var exception = Record.Exception(() => new CustomHttpClientFactory(config).Dispose());

        Assert.Null(exception);
    }

    [Fact]
    [Trait("slice", "3")]
    public void Content_headers_are_not_put_on_the_shared_client()
    {
        var config = new ArtilleryConfig
        {
            Headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Key"] = "abc"
            }
        };

        using var factory = new CustomHttpClientFactory(config);
        var client = factory.CreateClient();

        // Contains() throws for a content header name, so enumerate instead.
        Assert.DoesNotContain(client.DefaultRequestHeaders, h => h.Key == "Content-Type");
        Assert.Equal(["abc"], client.DefaultRequestHeaders.GetValues("X-Api-Key"));
    }

    [Fact]
    public void Request_headers_go_on_the_shared_client()
    {
        var config = new ArtilleryConfig
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "abc" }
        };

        using var factory = new CustomHttpClientFactory(config);

        Assert.Equal(["abc"], factory.CreateClient().DefaultRequestHeaders.GetValues("X-Api-Key"));
    }

    // The pool holds one connection per user the run can reach, instead of a fixed 200.
    [Fact]
    public void The_connection_limit_is_the_number_of_users()
    {
        using var flags = new CustomHttpClientFactory(new ArtilleryConfig { Vu = 64 });
        Assert.Equal(64, flags.MaxConnectionsPerServer);

        using var autoTune = new CustomHttpClientFactory(new ArtilleryConfig
        {
            Vu = 4, AutoTuneMaxRps = true, AutoTuneMaxClients = 300
        });
        Assert.Equal(300, autoTune.MaxConnectionsPerServer);

        using var yaml = new CustomHttpClientFactory(new ArtilleryConfig
        {
            Vu = 1,
            YamlConfig = new ConfigRoot { Phases = [new Phase { Vu = 10 }, new Phase { Vu = 25 }] }
        });
        Assert.Equal(25, yaml.MaxConnectionsPerServer);
    }
}
