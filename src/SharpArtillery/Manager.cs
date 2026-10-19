#pragma warning disable CA1848
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpArtillery.Configs;
using SharpArtillery.Models;

namespace SharpArtillery;

internal sealed class Manager : IDisposable
{
    private readonly List<DataPoint> _responseData = new();

    // Shared with the clients and the auto-tune task; touched only through Interlocked/Volatile.
    private long _totalRequests;
    private long _totalErrors;
    private long _claimedRequests;
    private long _cancelledAtPhaseEnd;
    private int _runningClients;

    private (int BestClients, double BestRps)? _autoTuneResult;

    /// <summary>
    /// Timer for current phase duration (restarted each phase).
    /// </summary>
    public readonly Stopwatch TotalTimeTimer = new();

    /// <summary>
    /// Global timer spanning all phases for report timeline (never reset).
    /// </summary>
    public readonly Stopwatch GlobalTimeTimer = new();

    /// <summary>
    /// Wall-clock time at which <see cref="GlobalTimeTimer"/> started. The clients derive every
    /// DateTime of a data point from it and the stopwatch, so one request never reads two clocks.
    /// </summary>
    public DateTime GlobalStartUtc { get; private set; }

    public Settings Settings { get; private set; } = new();
    private int _averageRps;
    private readonly ICustomHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private long _phaseStartedAt;

    /// <summary>
    /// Exposes rate limiting config for clients to create their own rate limiters.
    /// </summary>
    public int? ConstantRps => Settings.ConstantRps;

    /// <summary>
    /// Number of active clients (used by rate limiters for per-client interval calculation).
    /// </summary>
    public int ClientCount => _clients.Count;

    private readonly List<Client> _clients = new();
    private readonly List<Task> _clientTasks = new();

    // One channel, gate and template per phase; PrepareForTest replaces them.
    private Channel<DataPoint> _results = CreateResultChannel();
    private TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _clientsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RequestTemplate? _requestTemplate;

    public Manager(ICustomHttpClientFactory customHttpClientFactory, ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<Manager>();
        _httpClientFactory = customHttpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Clientcts = new CancellationTokenSource();
    }

    private CancellationTokenSource Clientcts { get; set; }
    public CancellationToken ClientKillToken => Clientcts.Token;

    /// <summary>
    /// Gets the auto-tune result if auto-tuning was enabled.
    /// </summary>
    public (int BestClients, double BestRps)? AutoTuneResult => _autoTuneResult;

    /// <summary>
    /// Gets the average RPS calculated after test completion.
    /// </summary>
    public int AverageRps => _averageRps;

    /// <summary>
    /// Completes when <see cref="RunTest"/> opens the phase; the clients send nothing before that.
    /// </summary>
    internal Task Started => _startGate.Task;

    /// <summary>
    /// Where the clients put each finished request.
    /// </summary>
    internal ChannelWriter<DataPoint> ResultWriter => _results.Writer;

    /// <summary>
    /// What a request of the current phase looks like; the clients build each request from it.
    /// </summary>
    internal RequestTemplate RequestTemplate =>
        _requestTemplate ?? throw new InvalidOperationException("PrepareForTest has not run.");

    public async Task ProcessData()
    {
        await Console.Out.WriteLineAsync("Processing data...");
        StatsUtils.AssignRps(_responseData);
    }

    public volatile Progress GetProgress = new();

    public List<DataPoint> Results => _responseData;
    private readonly ILogger<Manager> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public void Dispose()
    {
        Clientcts?.Dispose();
    }

    public ValueTask PrepareForTest(Settings settings)
    {
        Settings = settings;
        Settings.Validate();
        _autoTuneResult = null;
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        Interlocked.Exchange(ref _claimedRequests, 0);
        Interlocked.Exchange(ref _cancelledAtPhaseEnd, 0);
        // reset progress
        GetProgress = new();
        _results = CreateResultChannel();
        _startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Building one request up front surfaces a bad header or URL here, before any client runs,
        // instead of as one recorded error per request for the whole phase.
        Console.Out.WriteLine("Preparing requests...");
        _requestTemplate = new RequestTemplate(settings);
        _requestTemplate.Build().Dispose();

        // recreate clients per run so they bind to the current cancellation token
        Console.Out.WriteLine("Preparing clients...");
        Clientcts = new CancellationTokenSource();
        _clients.Clear();
        _clientTasks.Clear();
        for (var i = 0; i < settings.Vu; i++)
            _clients.Add(new Client(this, _httpClientFactory, _loggerFactory, clientIndex: i));

        if (settings.ConstantRps > 0)
        {
            var rpsPerClient = (double)settings.ConstantRps.Value / settings.Vu;
            Console.Out.WriteLine($"Rate limiting: {settings.ConstantRps} RPS across {settings.Vu} clients ({rpsPerClient:F1} RPS/client)");
        }

        // activate the clients (not starting test)
        Console.Out.WriteLine("Activating clients...");
        Interlocked.Exchange(ref _runningClients, _clients.Count);
        foreach (var client in _clients)
        {
            // No token on Task.Run: a task cancelled before it starts would never report itself
            // finished, and the phase end waits for every client.
            _clientTasks.Add(Task.Run(() => client.RunAsync(ClientKillToken)));
        }

        return new ValueTask(Task.CompletedTask);
    }

    /// <summary>
    /// Results the clients can have in the channel before a write waits for the reader. The reader
    /// drains at memory speed, so the backlog is normally a few items; the bound is there because an
    /// unbounded channel doubles its queue segment every time one fills, however fast it is drained,
    /// and that array of structs (a million slots after a long run) would stay alive into the report.
    /// </summary>
    private const int ResultBacklog = 65_536;

    private static Channel<DataPoint> CreateResultChannel() =>
        Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(ResultBacklog)
        {
            SingleReader = true,
            SingleWriter = false,
            // A writer waits for room rather than losing the result; the run loop keeps reading
            // until the last client has stopped, so the wait always ends.
            FullMode = BoundedChannelFullMode.Wait
        });

    /// <summary>
    /// Hands out one request slot. Always true in duration mode; in max-requests mode false once
    /// every slot of the phase is taken, even while earlier requests are still in flight.
    /// </summary>
    internal bool TryClaimRequest()
    {
        if (Settings.MaxRequests is not { } max) return true;
        return Interlocked.Increment(ref _claimedRequests) <= max;
    }

    /// <summary>
    /// A request was in flight when the phase ended. It has no outcome, so it is not a data point.
    /// </summary>
    internal void CountCancelledAtPhaseEnd() => Interlocked.Increment(ref _cancelledAtPhaseEnd);

    /// <summary>
    /// A client's loop has ended, for whatever reason; the last one lets the phase finish.
    /// </summary>
    internal void ClientFinished()
    {
        if (Interlocked.Decrement(ref _runningClients) == 0)
            _clientsDone.TrySetResult();
    }

    private void AddClients(int count)
    {
        if (count <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var clientIndex = _clients.Count; // New client gets next index
            var client = new Client(this, _httpClientFactory, _loggerFactory, clientIndex);
            _clients.Add(client);
            Interlocked.Increment(ref _runningClients);
            _clientTasks.Add(Task.Run(() => client.RunAsync(ClientKillToken)));
        }

        Settings.Vu += count;
    }

    private async Task RunAutoTuneAsync(AutoTuneSettings tune)
    {
        if (tune.MaxClients <= 0) throw new ArgumentException("Auto-tune max clients must be > 0");
        if (tune.Step <= 0) throw new ArgumentException("Auto-tune step must be > 0");
        if (tune.Interval <= TimeSpan.Zero) throw new ArgumentException("Auto-tune interval must be > 0");

        var bestRps = 0d;
        var bestClients = Settings.Vu;
        var stallCount = 0;

        while (!Clientcts.IsCancellationRequested)
        {
            var startRequests = Volatile.Read(ref _totalRequests);
            var startErrors = Volatile.Read(ref _totalErrors);
            var startClients = Settings.Vu;
            var intervalTimer = Stopwatch.StartNew();

            try
            {
                await Task.Delay(tune.Interval, _timeProvider, ClientKillToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Take a final partial sample (important for short runs where the last full interval never completes).
                var endRequestsCanceled = Volatile.Read(ref _totalRequests);
                var endErrorsCanceled = Volatile.Read(ref _totalErrors);

                var deltaRequestsCanceled = endRequestsCanceled - startRequests;
                var deltaErrorsCanceled = endErrorsCanceled - startErrors;
                var secondsCanceled = intervalTimer.Elapsed.TotalSeconds;

                if (secondsCanceled > 0 && deltaRequestsCanceled > 0)
                {
                    var rpsCanceled = deltaRequestsCanceled / secondsCanceled;
                    var errorRatioCanceled = (double)deltaErrorsCanceled / deltaRequestsCanceled;

                    if (rpsCanceled > bestRps * (1 + tune.MinImprovementRatio))
                    {
                        bestRps = rpsCanceled;
                        bestClients = startClients;
                    }

                    Console.Out.WriteLine(
                        $"AutoTune(final): clients={startClients}, rps={rpsCanceled:F1}, err={errorRatioCanceled:P1}, best={bestRps:F1} @ {bestClients}");
                }

                break;
            }

            var endRequests = Volatile.Read(ref _totalRequests);
            var endErrors = Volatile.Read(ref _totalErrors);

            var deltaRequests = endRequests - startRequests;
            var deltaErrors = endErrors - startErrors;

            var seconds = tune.Interval.TotalSeconds;
            var rps = seconds > 0 ? deltaRequests / seconds : 0;
            var errorRatio = deltaRequests > 0 ? (double)deltaErrors / deltaRequests : 0;

            if (rps > bestRps * (1 + tune.MinImprovementRatio))
            {
                bestRps = rps;
                bestClients = startClients;
                stallCount = 0;
            }
            else
            {
                stallCount++;
            }

            Console.Out.WriteLine(
                $"AutoTune: clients={startClients}, rps={rps:F1}, err={errorRatio:P1}, best={bestRps:F1} @ {bestClients}");

            if (errorRatio > tune.MaxErrorRatio)
            {
                _autoTuneResult = (bestClients, bestRps);
                Console.Out.WriteLine(
                    $"AutoTune: stopping (error ratio {errorRatio:P1} > {tune.MaxErrorRatio:P1}). Best was {bestRps:F1} rps @ {bestClients} clients.");
                Clientcts.Cancel();
                break;
            }

            if (stallCount >= tune.MaxStallIntervals)
            {
                _autoTuneResult = (bestClients, bestRps);
                Console.Out.WriteLine(
                    $"AutoTune: stopping (no improvement). Best was {bestRps:F1} rps @ {bestClients} clients.");
                break;
            }

            if (Settings.Vu >= tune.MaxClients)
            {
                _autoTuneResult = (bestClients, bestRps);
                Console.Out.WriteLine(
                    $"AutoTune: reached max clients ({tune.MaxClients}). Best was {bestRps:F1} rps @ {bestClients} clients.");
                break;
            }

            var toAdd = Math.Min(tune.Step, tune.MaxClients - Settings.Vu);
            AddClients(toAdd);
        }

        _autoTuneResult ??= (bestClients, bestRps);
    }

    /// <summary>
    /// What arrived since the last progress line; Rps and the mean latency on the progress line
    /// are about this interval, the request count and error ratio about the whole phase.
    /// </summary>
    private struct IntervalStats
    {
        public int Requests;
        public int OkRequests;
        public double OkLatencyMs;
    }

    /// <summary>
    /// Runs the phase prepared by <see cref="PrepareForTest"/>: opens the start gate, collects
    /// the clients' results until the phase ends, then makes sure nothing is left in flight.
    /// </summary>
    public async Task RunTest()
    {
        Console.Out.WriteLine("Starting clients...");

        // Don't clear _responseData - accumulate across phases for multi-phase YAML tests
        TotalTimeTimer.Restart();
        _phaseStartedAt = _timeProvider.GetTimestamp();

        // Start global timer only once (spans all phases for report timeline)
        if (!GlobalTimeTimer.IsRunning)
        {
            GlobalStartUtc = DateTime.UtcNow;
            GlobalTimeTimer.Start();
        }

        // The duration counts from the moment the gate opens, not from PrepareForTest.
        using var phaseEnd = Settings.Duration is { } duration
            ? _timeProvider.CreateTimer(static cts => ((CancellationTokenSource)cts!).Cancel(), Clientcts,
                duration, Timeout.InfiniteTimeSpan)
            : null;
        _startGate.TrySetResult();

        Task? autoTuneTask = null;
        if (Settings.AutoTune != null)
        {
            Console.Out.WriteLine(
                $"AutoTune enabled: maxClients={Settings.AutoTune.MaxClients}, step={Settings.AutoTune.Step}, interval={Settings.AutoTune.Interval}, maxErr={Settings.AutoTune.MaxErrorRatio:P1}");
            autoTuneTask = Task.Run(() => RunAutoTuneAsync(Settings.AutoTune));
        }

        var reader = _results.Reader;
        var interval = new IntervalStats();
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        var tick = ticker.WaitForNextTickAsync().AsTask();
        var readable = reader.WaitToReadAsync().AsTask();
        var clientsDone = _clientsDone.Task;

        // The loop sleeps until a result arrives, a second passes or the last client stops.
        // Results are read before a tick is handled, so a progress line covers everything that had
        // arrived when its second ended.
        while (true)
        {
            await Task.WhenAny(readable, tick, clientsDone).ConfigureAwait(false);

            if (readable.IsCompleted)
            {
                ReadAvailable(reader, ref interval);
                readable = reader.WaitToReadAsync().AsTask();
            }

            if (tick.IsCompleted)
            {
                PublishProgress(ref interval);
                tick = ticker.WaitForNextTickAsync().AsTask();
            }

            if (clientsDone.IsCompleted) break;
        }

        // Phase end: cancel what is still in flight, wait for every client to stop, and only then
        // complete the channel, so no result can be written after the final drain. A result that
        // arrived after the loop would otherwise be counted in the next phase.
        Clientcts.Cancel();
        if (autoTuneTask != null)
        {
            try
            {
                await autoTuneTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await Task.WhenAll(_clientTasks).ConfigureAwait(false);
        _results.Writer.Complete();
        ReadAvailable(reader, ref interval);
        PublishProgress(ref interval);

        _averageRps = StatsUtils.AverageRps(GetProgress.Requests, TotalTimeTimer.Elapsed);
        TotalTimeTimer.Stop();
    }

    private void ReadAvailable(ChannelReader<DataPoint> reader, ref IntervalStats interval)
    {
        var requests = 0;
        var errors = 0;
        while (reader.TryRead(out var data))
        {
            _responseData.Add(data);
            requests++;
            if (data.IsError())
            {
                errors++;
            }
            else
            {
                interval.OkRequests++;
                interval.OkLatencyMs += data.ResponseTime.TotalMilliseconds;
            }
        }

        if (requests == 0) return;

        interval.Requests += requests;
        Interlocked.Add(ref _totalRequests, requests);
        if (errors > 0)
            Interlocked.Add(ref _totalErrors, errors);
    }

    private void PublishProgress(ref IntervalStats interval)
    {
        var requests = (int)Volatile.Read(ref _totalRequests);
        var errors = (int)Volatile.Read(ref _totalErrors);
        GetProgress = new Progress
        {
            Requests = requests,
            ErrorRatio = requests > 0 ? (float)errors / requests : 0f,
            Rps = interval.Requests,
            MeanLatency = interval.OkRequests > 0 ? (float)(interval.OkLatencyMs / interval.OkRequests) : 0f,
            PercentDone = PercentDone(requests),
            CancelledAtPhaseEnd = (int)Volatile.Read(ref _cancelledAtPhaseEnd)
        };
        interval = default;
    }

    private int PercentDone(int phaseRequests)
    {
        // The duration share follows the same clock as the duration timer, so both agree on the end.
        var done = Settings.MaxRequests is > 0 and var max
            ? phaseRequests * 100L / max
            : (long)(_timeProvider.GetElapsedTime(_phaseStartedAt) / Settings.Duration!.Value * 100);
        return (int)Math.Min(100, done);
    }
}

/// <summary>
/// What every request of a phase looks like. Clients build each request from it just before
/// sending it, so no request message exists ahead of its turn.
/// </summary>
internal sealed class RequestTemplate
{
    private readonly HttpMethod _method;
    private readonly Uri _target;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly object? _jsonContent;
    private readonly string? _rawBody;

    /// <summary>
    /// <c>Content-*</c> headers describe the body, so they go on the request's content, not on the request.
    /// </summary>
    internal static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    public RequestTemplate(Settings settings)
    {
        _method = settings.Method?.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            null => HttpMethod.Get,
            _ => HttpMethod.Get
        };
        _target = new Uri(settings.Target ?? throw new ArgumentException("Target is missing."),
            UriKind.RelativeOrAbsolute);
        _headers = settings.Headers is { Count: > 0 } headers ? [.. headers] : [];
        _jsonContent = settings.JsonContent;
        _rawBody = settings.RawBody;
    }

    public HttpRequestMessage Build()
    {
        var req = new HttpRequestMessage(_method, _target);
        foreach (var (name, value) in _headers)
        {
            if (!IsContentHeader(name))
                req.Headers.Add(name, value);
        }

        if (_jsonContent != null)
        {
            req.Content = JsonContent.Create(_jsonContent);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else if (_rawBody != null)
        {
            req.Content = new StringContent(_rawBody);
        }
        else if (_headers.Any(h => IsContentHeader(h.Key)))
        {
            // A content header with no body still reaches the server, on an empty body.
            req.Content = new ByteArrayContent(Array.Empty<byte>());
        }

        // Set after the default content type, so a -H Content-Type replaces it.
        if (req.Content != null)
        {
            foreach (var (name, value) in _headers)
            {
                if (!IsContentHeader(name)) continue;
                req.Content.Headers.Remove(name);
                req.Content.Headers.Add(name, value);
            }
        }

        return req;
    }
}

internal sealed class Progress
{
    public int Requests { get; set; }
    public int PercentDone { get; set; }
    public float MeanLatency { get; set; }
    public int Rps { get; set; }
    public float ErrorRatio { get; set; }

    /// <summary>
    /// Requests still in flight when the phase ended. They are not in the results.
    /// </summary>
    public int CancelledAtPhaseEnd { get; set; }
}
