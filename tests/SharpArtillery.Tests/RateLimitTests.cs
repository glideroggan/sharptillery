using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpArtillery.Configs;
using SharpArtillery.Tests.Stubs;
using Xunit;

namespace SharpArtillery.Tests;

/// <summary>
/// <c>-r</c> as the clients run it. Before slice 7 every client slept "its share of the interval" after its own
/// response, so a slow answer or the send itself came on top: <c>-c 2 -r 200</c> reached 135 req/s and
/// <c>-c 10 -r 100</c> against a 50 ms endpoint 63 req/s. The clients now share one schedule, slot n due at
/// the phase start + n / rps.
/// </summary>
public class RateLimitTests
{
    private const string Target = "http://stub.test/x";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // A step waits this long for every client to wait on the clock again; it is real time, not run time.
    private static readonly TimeSpan ParkPatience = TimeSpan.FromSeconds(5);

    private static StubHttpHandler Answering(TimeProvider clock, TimeSpan latency) =>
        new(async (_, ct) =>
        {
            await Task.Delay(latency, clock, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    /// <summary>
    /// Runs a prepared duration phase on <paramref name="clock"/>, one <paramref name="step"/> at a time. A step
    /// is taken only when every client waits on the clock, plus the phase's own duration timer, and nothing
    /// that is due has been left unfired.
    /// </summary>
    private static async Task DriveAsync(Manager manager, ParkingClock clock, int clients, TimeSpan step)
    {
        var run = manager.RunTest();
        var parked = clients + 1;
        while (!run.IsCompleted)
        {
            var settled = SpinWait.SpinUntil(() => run.IsCompleted || clock.Parked(parked), ParkPatience);
            Assert.True(settled,
                $"the clients did not all wait on the run's clock ({clock.PendingOneShots} of {parked} waits)");
            if (run.IsCompleted) break;
            clock.Advance(step);
        }

        await run.WaitAsync(Patience);
    }

    private static Settings RatePhase(int clients, int rps, string? phaseName = null) => new()
    {
        Target = Target,
        Vu = clients,
        ConstantRps = rps,
        Duration = TimeSpan.FromSeconds(1),
        RequestTimeout = TimeSpan.FromSeconds(30),
        PhaseName = phaseName
    };

    [Fact]
    [Trait("slice", "7")]
    public async Task Two_clients_reach_200_per_second()
    {
        var clock = new ParkingClock();
        var stub = Answering(clock, TimeSpan.Zero);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        await manager.PrepareForTest(RatePhase(clients: 2, rps: 200));
        await DriveAsync(manager, clock, clients: 2, TimeSpan.FromMilliseconds(1));

        // Slots 0..199 fall inside the second; slot 200 is due on its last tick.
        Assert.InRange(stub.CallCount, 200, 201);
        Assert.Null(manager.RateShortfall);
    }

    [Fact]
    [Trait("slice", "7")]
    public async Task A_slow_endpoint_still_gets_the_rate_when_there_are_enough_clients()
    {
        var clock = new ParkingClock();
        var stub = Answering(clock, TimeSpan.FromMilliseconds(50));
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        // 10 clients at 50 ms can do 200 req/s, so 100 req/s is reachable.
        await manager.PrepareForTest(RatePhase(clients: 10, rps: 100));
        await DriveAsync(manager, clock, clients: 10, TimeSpan.FromMilliseconds(1));

        Assert.InRange(stub.CallCount, 100, 101);
        // The requests sent in the last 50 ms were still out when the phase ended.
        Assert.Equal(stub.CallCount, manager.Results.Count + manager.GetProgress.CancelledAtPhaseEnd);
        Assert.Null(manager.RateShortfall);
    }

    [Fact]
    [Trait("slice", "7")]
    public async Task Too_few_clients_for_the_latency_fall_short_and_say_why()
    {
        var clock = new ParkingClock();
        var stub = Answering(clock, TimeSpan.FromMilliseconds(50));
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        // 2 clients at 50 ms can do 40 req/s; the schedule asks for 100.
        await manager.PrepareForTest(RatePhase(clients: 2, rps: 100));
        await DriveAsync(manager, clock, clients: 2, TimeSpan.FromMilliseconds(1));

        Assert.True(stub.CallCount is >= 40 and <= 42, $"{stub.CallCount} requests");
        Assert.NotNull(manager.RateShortfall);
        Assert.True(Regex.IsMatch(manager.RateShortfall!,
                @"^Rate: target 100 req/s, reached (38|39|40) req/s\. 2 clients at 50 ms per request can do at most 40 req/s; more clients \(-c\) can reach the target\.$"),
            manager.RateShortfall);
    }

    [Fact]
    [Trait("slice", "7")]
    public async Task Each_phase_starts_its_own_schedule()
    {
        var clock = new ParkingClock();
        var stub = Answering(clock, TimeSpan.Zero);
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance, clock);

        await manager.PrepareForTest(RatePhase(clients: 2, rps: 100, phaseName: "A"));
        await DriveAsync(manager, clock, clients: 2, TimeSpan.FromMilliseconds(1));
        var phaseA = stub.CallCount;

        // Five seconds between the phases: a schedule kept from phase A would owe 500 slots and send them at once.
        clock.Advance(TimeSpan.FromSeconds(5));

        await manager.PrepareForTest(RatePhase(clients: 2, rps: 100, phaseName: "B"));
        await DriveAsync(manager, clock, clients: 2, TimeSpan.FromMilliseconds(1));

        Assert.InRange(phaseA, 100, 101);
        Assert.InRange(stub.CallCount - phaseA, 100, 101);
    }

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

    // Real time: one client at 50 ms per request can do 20 req/s, a fifth of the target.
    [Fact]
    [Trait("slice", "7")]
    public async Task A_target_the_clients_cannot_reach_ends_with_one_line_that_says_so()
    {
        var stub = Answering(TimeProvider.System, TimeSpan.FromMilliseconds(50));
        using var factory = new StubHttpClientFactory(stub);
        var flags = new Configs.ArtilleryConfig { Target = Target, Vu = 1, MaxRequests = 5, ConstantRps = 100 };

        var output = await CaptureConsoleAsync(() => new MainService(NullLoggerFactory.Instance).Run(flags, factory));

        Assert.Equal(5, stub.CallCount);
        Assert.Matches(
            @"target 100 req/s, reached \d+ req/s\. 1 client at [\d.]+ ms per request can do at most \d+ req/s",
            output);
    }
}
