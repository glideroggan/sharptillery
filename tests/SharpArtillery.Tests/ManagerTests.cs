using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.Tests.Stubs;
using Xunit;

namespace SharpArtillery.Tests;

public class ManagerTests
{
    private const string Target = "http://stub.test/x";

    private static async Task RunPhaseAsync(Manager manager, Settings settings)
    {
        await manager.PrepareForTest(settings);
        var thread = new Thread(manager.RunTest) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the run did not end");
        await manager.ProcessData();
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

    // Red for slice 2: a request still in flight when the phase ends is cancelled, and the
    // client's catch-all records it as a 500 (58 fake errors with -c 256). It should be dropped
    // and counted as cancelled at phase end. The 500 can land in Results or, when it is enqueued
    // after the run loop has stopped, stay in ResponseMessageQueue, so both are checked.
    [Fact(Skip = "slice 2")]
    [Trait("slice", "2")]
    public async Task A_request_cut_off_at_phase_end_is_not_a_500()
    {
        var stub = StubHttpHandler.Hanging();
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings
        {
            Target = Target,
            Vu = 4,
            Duration = TimeSpan.FromMilliseconds(300),
            RequestTimeout = TimeSpan.FromSeconds(30)
        });
        await Task.Delay(500); // let the cancelled clients finish recording

        Assert.True(stub.CallCount > 0, "no request was in flight at phase end");
        var recorded = manager.Results.Concat(manager.ResponseMessageQueue).ToList();
        Assert.DoesNotContain(recorded, d => d.Status == HttpStatusCode.InternalServerError);
    }

    // Red for slice 2: the phase's "% errors" is the error count of the last one-second
    // interval divided by every request of the phase. Errors only in the first half second
    // of a two-second phase show as about 0%.
    [Fact(Skip = "slice 2")]
    [Trait("slice", "2")]
    public async Task The_phase_error_ratio_counts_the_whole_phase()
    {
        var clock = Stopwatch.StartNew();
        var stub = new StubHttpHandler(async (_, ct) =>
        {
            await Task.Delay(5, ct).ConfigureAwait(false);
            return new HttpResponseMessage(clock.Elapsed < TimeSpan.FromMilliseconds(500)
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK);
        });
        using var factory = new StubHttpClientFactory(stub);
        using var manager = new Manager(factory, NullLoggerFactory.Instance);

        await RunPhaseAsync(manager, new Settings
        {
            Target = Target,
            Vu = 4,
            Duration = TimeSpan.FromSeconds(2)
        });

        var results = manager.Results;
        var actualRatio = (double)results.Count(d => d.IsError()) / results.Count;
        Assert.True(actualRatio > 0.1, $"setup: expected early errors, got {actualRatio:P1}");
        Assert.Equal(actualRatio, manager.GetProgress.ErrorRatio, 0.02);
    }

    // Red for slice 3: `-H Content-Type:...` throws "Misused header name" while the request
    // queue is built, because Content-* headers belong on the content, not the request.
    [Fact(Skip = "slice 3")]
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
}

public class CustomHttpClientFactoryTests
{
    // Red for slice 3: the factory puts every -H header on DefaultRequestHeaders, and
    // Content-Type throws "Misused header name" there, so the tool crashes at start.
    [Fact(Skip = "slice 3")]
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
    public void Request_headers_go_on_the_shared_client()
    {
        var config = new ArtilleryConfig
        {
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "abc" }
        };

        using var factory = new CustomHttpClientFactory(config);

        Assert.Equal(["abc"], factory.CreateClient().DefaultRequestHeaders.GetValues("X-Api-Key"));
    }
}
