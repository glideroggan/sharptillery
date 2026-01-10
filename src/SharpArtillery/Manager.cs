#pragma warning disable CA1848
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpArtillery.Configs;
using SharpArtillery.Models;

namespace SharpArtillery;

internal sealed class Manager : IDisposable
{
    private volatile List<DataPoint> _responseData = new();

    private long _totalRequests;
    private long _totalErrors;
    private (int BestClients, double BestRps)? _autoTuneResult;

    /// <summary>
    /// Timer for current phase duration (restarted each phase).
    /// </summary>
    public readonly Stopwatch TotalTimeTimer = new();
    
    /// <summary>
    /// Global timer spanning all phases for report timeline (never reset).
    /// </summary>
    public readonly Stopwatch GlobalTimeTimer = new();
    
    public Settings Settings { get; private set; } = new();
    private int _averageRps;
    private readonly ICustomHttpClientFactory _httpClientFactory;

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
    public readonly ConcurrentQueue<HttpRequestMessage> RequestMessageQueue = new();
    public readonly ConcurrentQueue<DataPoint> ResponseMessageQueue = new();

    private ManualResetEvent _done = new(false);

    public Manager(ICustomHttpClientFactory customHttpClientFactory, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<Manager>();
        _httpClientFactory = customHttpClientFactory;
        Clientcts = new CancellationTokenSource();
    }

    private sealed record TopupSettings(int WarningLimit, int TopupAmount, int TopupLimit);

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

    public async Task ProcessData()
    {
        await Console.Out.WriteLineAsync("Processing data...");

        // Calculate RPS for each data point using O(n) sliding window algorithm.
        // For each point, count how many requests completed within the 1-second window
        // ending at that point's completion time: (point.Time - 1s, point.Time]
        _responseData.Sort((a, b) =>
            a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
            a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

        var left = 0;
        for (var right = 0; right < _responseData.Count; right++)
        {
            var point = _responseData[right];
            var windowStart = point.RequestReceivedTime.AddSeconds(-1);

            // Advance left pointer past entries at or before windowStart
            // This matches the original condition: (windowStart < entry.Time]
            while (left <= right && _responseData[left].RequestReceivedTime <= windowStart)
                left++;

            point.Rps = right - left + 1;
            _responseData[right] = point;
        }

        _done.WaitOne();
    }

    public volatile Progress GetProgress = new();

    public List<DataPoint> Results => _responseData;
    internal readonly ManualResetEvent StartClients = new(false);
    private readonly ILogger<Manager> _logger;
    private TopupSettings _topupSettings = new(WarningLimit: 0, TopupAmount: 0, TopupLimit: 0);
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
        // reset progress
        GetProgress = new();
        // Reset StartClients gate for this phase (clients wait until RunTest sets it)
        StartClients.Reset();
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

        // configure the request queue settings
        _topupSettings = new TopupSettings(2000, 4000 * Settings.Vu, 4000);

        // create request queue
        Console.Out.WriteLine("Preparing requests...");
        PrepareRequestQueue();

        // activate the clients (not starting test)
        Console.Out.WriteLine("Activating clients...");
        foreach (var client in _clients)
        {
            _clientTasks.Add(Task.Run(() => client.RunAsync(ClientKillToken), ClientKillToken));
        }

        return new ValueTask(Task.CompletedTask);
    }

    private void AddClients(int count)
    {
        if (count <= 0) return;
        for (var i = 0; i < count; i++)
        {
            var clientIndex = _clients.Count; // New client gets next index
            var client = new Client(this, _httpClientFactory, _loggerFactory, clientIndex);
            _clients.Add(client);
            _clientTasks.Add(Task.Run(() => client.RunAsync(ClientKillToken), ClientKillToken));
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
                await Task.Delay(tune.Interval, ClientKillToken).ConfigureAwait(false);
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

    private void PrepareRequestQueue()
    {
        // TODO: put in trace logs
        // TODO: handle constant RPS
        RequestMessageQueue.Clear();

        if (Settings.MaxRequests.HasValue)
        {
            // TODO: make sure its not too many requests in MaxRequests, in that case we want to top up when going low
            TopUpRequestQueue(Settings.MaxRequests.Value);
        }
        else if (Settings.Duration.HasValue)
        {
            // NOTE: how many requests to start with? depends on duration and how many clients that will go
            TopUpRequestQueue(_topupSettings.TopupAmount);
        }
    }

    private void TopUpRequestQueue(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var method = Settings.Method?.ToUpperInvariant() switch
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
            var req = new HttpRequestMessage(method, Settings.Target);
            if (Settings.Headers != null)
            {
                foreach (var header in Settings.Headers)
                {
                    req.Headers.Add(header.Key, header.Value);
                }
            }

            if (Settings.JsonContent != null)
            {
                req.Content = JsonContent.Create(Settings.JsonContent);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            RequestMessageQueue.Enqueue(req);
        }
    }

    private void UpdateReport(ref Stopwatch timer, ref int accumulatedRequests, ref int accumulatedErrors,
        ref float accumulatedLatency)
    {
        GetProgress.Requests += accumulatedRequests;
        GetProgress.ErrorRatio = GetProgress.Requests > 0
            ? (float)accumulatedErrors / GetProgress.Requests
            : 0f;
        GetProgress.Rps = accumulatedRequests;
        GetProgress.MeanLatency = accumulatedRequests > 0
            ? accumulatedLatency / accumulatedRequests
            : 0f;
        // TODO: needs to change to handle <duration>
        GetProgress.PercentDone = Settings.MaxRequests > 0
            ? (int)(_responseData.Count / (float)Settings.MaxRequests * 100)
            : (int)(TotalTimeTimer.ElapsedMilliseconds / Settings.Duration!.Value.TotalMilliseconds * 100);

        accumulatedErrors = 0;
        accumulatedLatency = 0f;
        accumulatedRequests = 0;
        timer.Restart();
    }

    public void RunTest()
    {
        Console.Out.WriteLine("Starting clients...");
        StartClients.Set();

        // Track the starting count for multi-phase assertion (data accumulates across phases)
        var phaseStartCount = _responseData.Count;
        
        // Don't clear _responseData - accumulate across phases for multi-phase YAML tests
        TotalTimeTimer.Restart();
        
        // Start global timer only once (spans all phases for report timeline)
        if (!GlobalTimeTimer.IsRunning)
            GlobalTimeTimer.Start();

        Task? autoTuneTask = null;
        if (Settings.AutoTune != null)
        {
            Console.Out.WriteLine(
                $"AutoTune enabled: maxClients={Settings.AutoTune.MaxClients}, step={Settings.AutoTune.Step}, interval={Settings.AutoTune.Interval}, maxErr={Settings.AutoTune.MaxErrorRatio:P1}");
            autoTuneTask = Task.Run(() => RunAutoTuneAsync(Settings.AutoTune));
        }

        var timer = Stopwatch.StartNew();
        var accumulatedErrors = 0;
        var accumulatedMeanLatency = 0f;
        var accumulatedRequests = 0;
        while (true)
        {
            // we're done?
            if (!Clientcts.IsCancellationRequested &&
                Settings.MaxRequests.HasValue && Settings.MaxRequests == (_responseData.Count - phaseStartCount))
                Clientcts.Cancel();
            if (Settings.Duration.HasValue && TotalTimeTimer.Elapsed > Settings.Duration)
                Clientcts.Cancel();

            if (Settings.Duration.HasValue)
            {
                if (RequestMessageQueue.Count < _topupSettings.WarningLimit)
                {
                    _logger.LogWarning("Too few topups, request queue too low");
                    _topupSettings = _topupSettings with { TopupAmount = _topupSettings.TopupAmount + 1000 };
                    TopUpRequestQueue(_topupSettings.TopupAmount);
                }

                // top up requests (once we are not filling up the queue at once)
                if (RequestMessageQueue.Count < _topupSettings.TopupLimit)
                {
                    TopUpRequestQueue(_topupSettings.TopupAmount);
                }
            }


            if (Clientcts.IsCancellationRequested && ResponseMessageQueue.IsEmpty) break; // quit testing

            DequeueAndAccumulate(ref accumulatedRequests, ref accumulatedErrors, ref accumulatedMeanLatency);
            if (timer.Elapsed.TotalSeconds >= 1)
            {
                UpdateReport(ref timer, ref accumulatedRequests, ref accumulatedErrors, ref accumulatedMeanLatency);
            }
        }

        Debug.Assert(ResponseMessageQueue.IsEmpty);
        UpdateReport(ref timer, ref accumulatedRequests, ref accumulatedErrors, ref accumulatedMeanLatency);
        Debug.Assert(_responseData.Count - phaseStartCount == GetProgress.Requests);

        _averageRps = (int)(GetProgress.Requests / TotalTimeTimer.Elapsed.TotalSeconds);

        _done.Set();
        TotalTimeTimer.Stop();

        if (autoTuneTask != null)
        {
            try
            {
                autoTuneTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // ignore
            }
        }
    }

    private void DequeueAndAccumulate(ref int accumulatedRequests, ref int accumulatedErrors,
        ref float accumulatedMeanLatency)
    {
        var results = new List<DataPoint>();
        while (!ResponseMessageQueue.IsEmpty)
        {
            if (!ResponseMessageQueue.TryDequeue(out var res))
                continue;
            results.Add(res);
        }

        accumulatedRequests += results.Count;

        if (results.Count > 0)
            Interlocked.Add(ref _totalRequests, results.Count);

        var intervalErrors = 0;

        foreach (var data in results)
        {
            _responseData.Add(data);
            if (data.IsError())
            {
                accumulatedErrors++;
                intervalErrors++;
            }
        }

        if (intervalErrors > 0)
            Interlocked.Add(ref _totalErrors, intervalErrors);

        accumulatedMeanLatency += (float)results.Where(x => !x.IsError())
            .Sum(x => x.ResponseTime.TotalMilliseconds);
    }
}

internal sealed class Progress
{
    public int Requests { get; set; }
    public int PercentDone { get; set; }
    public float MeanLatency { get; set; }
    public int Rps { get; set; }
    public float ErrorRatio { get; set; }
}