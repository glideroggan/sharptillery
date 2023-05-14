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

/*
 * BUG:
 *  - Why do we get more latency running with more clients and having low constant rps?
 *      Looking at the server it still only have low connection rate, meaning that most clients aren't even sending.
 *      Maybe we can try starting each client with its own thread? Just to see if latency would go down.
 *          If it does, it probably means that a thread would get more scheduling time from cpu than a task does
 * TODO:
 *  Reporting:
 *      - take snapshots at 1s and in the reporting graph we can average all values
 *          this way we get a nicer graph
 *      - Add target endpoint targeted
 *      - Add the main things about a load test (https://www.freecodecamp.org/news/practical-guide-to-load-testing/)
 *          into the table of the html report
 * 
 */

[Flags]
internal enum FlagEnum
{
    None = 0,
    ConstantRps
}

internal class Manager : IDisposable
{
    private volatile List<DataPoint> _responseData = new();

    public readonly Stopwatch TotalTimeTimer = new();
    private FlagEnum _flags;
    private Settings _settings;
    private int _averageRps;
    private SemaphoreSlim _blocker;
    private readonly ICustomHttpClientFactory _httpClientFactory;

    private readonly List<Client> _clients = new();
    public readonly ConcurrentQueue<HttpRequestMessage> RequestMessageQueue = new();
    public readonly ConcurrentQueue<DataPoint> ResponseMessageQueue = new();

    private ManualResetEvent Done = new(false);

    public Manager(ICustomHttpClientFactory customHttpClientFactory, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<Manager>();
        _httpClientFactory = customHttpClientFactory;
    }

    record TopupSettings(int WarningLimit, int TopupAmount, int TopupLimit);

    private CancellationTokenSource Clientcts { get; set; }
    public CancellationToken ClientKillToken => Clientcts.Token;


    public void Report()
    {
        int GetPercentage(List<double> doubles, float percent)
        {
            return (int)MathF.Round(percent * doubles.Count) >= doubles.Count
                ? doubles.Count - 1
                : (int)MathF.Round(percent * doubles.Count);
        }

        var okRequests = _responseData.Where(x => !IsError(x)).ToList();
        var errorRequests = _responseData.Where(IsError).ToList();


        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Target URL:", _settings.Target));
        Console.Out.WriteLine(_settings.MaxRequests > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Max requests:", _settings.MaxRequests)
            : string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Duration:",
                _settings.Duration!.Value.ToString()));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Concurrency level:",
            _settings.Vu));
        Console.Out.WriteLine();

        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Completed requests:",
            okRequests.Count + errorRequests.Count));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total errors:",
            errorRequests.Count));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total time:",
            $"{TotalTimeTimer.Elapsed.TotalSeconds} s"));
        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Requests per second:",
            _averageRps));
        var mean = _responseData.Count > 0 ? okRequests.Average(r => r.ResponseTime.TotalMilliseconds) : 0;
        Console.Out.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Mean latency:", $"{mean:F} ms"));
        Console.Out.WriteLine();

        Console.Out.WriteLine("Percentage of the OK requests served within a certain time");
        var t = okRequests.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        t.Sort();
        if (t.Count == 0)
        {
            // there were no completed requests at all
            Console.Out.WriteLine("No requests went fine!");
        }
        else
        {
            // 50% take median
            var i = GetPercentage(t, .5f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "50%", $"{t[i]:F} ms"));
            // 90%
            i = GetPercentage(t, .9f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "90%", $"{t[i]:F} ms"));
            // 95%
            i = GetPercentage(t, .95f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "95%", $"{t[i]:F} ms"));
            // 99% divide all response-time into 100
            i = GetPercentage(t, .99f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "99%", $"{t[i]:F} ms"));
            // 100% take highest response-time, everything is faster than this
            Console.Out.WriteLine(
                string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "100%", $"{t[^1]:F} ms"));
        }

        Console.Out.WriteLine("Percentage of the ERROR requests served within a certain time");
        var errorList = errorRequests.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        errorList.Sort();
        if (errorList.Count == 0)
        {
            // there were no completed requests at all
            Console.Out.WriteLine("No Errors");
        }
        else
        {
            // 50% take median
            var i = GetPercentage(errorList, .5f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "50%",
                $"{errorList[i]:F} ms"));
            // 90%
            i = GetPercentage(errorList, .9f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "90%",
                $"{errorList[i]:F} ms"));
            // 95%
            i = GetPercentage(errorList, .95f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "95%",
                $"{errorList[i]:F} ms"));
            // 99% divide all response-time into 100
            i = GetPercentage(errorList, .99f);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "99%",
                $"{errorList[i]:F} ms"));
            // 100% take highest response-time, everything is faster than this
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "100%",
                $"{errorList[^1]:F} ms"));
        }
    }

    public async Task ProcessData()
    {
        await Console.Out.WriteAsync("Processing data...");

        // calculate the RPS for each request data by looking at each data point and take all other points
        // before it (up to a second) and count the number of requests. This should be the accurate number of rps
        var timer = Stopwatch.StartNew();
        _responseData.Sort((a, b) =>
            a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
            a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

        int? lastCountedNumerOfRequests = null;
        int? lastIndex = null;
        var startReached = false;
        for (var index = _responseData.Count - 1; index >= 0; index--)
        {
            var point = _responseData[index];
            if (startReached)
            {
                lastCountedNumerOfRequests = lastCountedNumerOfRequests == 0 ? 0 : lastCountedNumerOfRequests - 1;
            }

            var numOfRequests = lastCountedNumerOfRequests ?? 0;

            if (!startReached)
            {
                for (var i2 = lastIndex ?? index; i2 >= 0; i2--)
                {
                    var isValid = point.RequestReceivedTime.AddSeconds(-1) < _responseData[i2].RequestReceivedTime &&
                                  _responseData[i2].RequestReceivedTime <= point.RequestReceivedTime;
                    if (!isValid)
                    {
                        lastIndex = i2;
                        lastCountedNumerOfRequests = numOfRequests - 1;

                        break;
                    }

                    numOfRequests++;
                    if (i2 == 0)
                    {
                        startReached = true;
                    }
                }
            }

            point.Rps = numOfRequests;
            _responseData[index] = point;
            if (!(timer.Elapsed.TotalSeconds > 1)) continue;

            Console.Out.Write(".");
            timer.Restart();
        }

        Console.Out.WriteLine();

        Done.WaitOne();
    }

    public volatile Progress GetProgress = new();

    public List<DataPoint> Results => _responseData;
    internal readonly ManualResetEvent StartClients = new(false);
    private readonly ILogger<Manager> _logger;
    private TopupSettings _topupSettings;
    private readonly ILoggerFactory _loggerFactory;

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask PrepareForTest(Settings settings)
    {
        _settings = settings;
        // reset progress
        GetProgress = new();
        // TODO: continue here, we're clearing clients here, but maybe we should check first....
        Console.Out.WriteLine("Preparing clients...");
        // create clients
        Clientcts = new CancellationTokenSource();
        while (_clients.Count < settings.Vu)
        {
            var c = new Client(this, _httpClientFactory, _loggerFactory);
            _clients.Add(c);
        }

        // TODO: not implemented yet into the flow
        if (settings.ConstantRps > 0)
        {
            _blocker = new SemaphoreSlim(settings.ConstantRps.Value, settings.ConstantRps.Value);
            _flags |= FlagEnum.ConstantRps;
        }

        Debug.Assert(settings.Duration == null && settings.MaxRequests > 0 ||
                     settings is { Duration: { }, MaxRequests: null }, "They are exclusive each other. " +
                                                                       "This should be taken care of in the parsing" +
                                                                       "of the yaml/flags.");
        // if (settings.Duration != null && settings.MaxRequests > 0)
        //     throw new ArgumentException("You can't have flag duration and requests at the same time");
        // configure the request queue settings
        _topupSettings = new TopupSettings(2000, 4000 * _settings.Vu, 4000);

        // create request queue
        Console.Out.WriteLine("Preparing requests...");
        PrepareRequestQueue();

        // activate the clients (not starting test)
        Console.Out.WriteLine("Activating clients...");
        foreach (var client in _clients)
        {
            var clientThread = new Thread(client.Run) { IsBackground = true };
            clientThread.Start();
        }

        return new ValueTask(Task.CompletedTask);
    }

    private void PrepareRequestQueue()
    {
        // TODO: put in trace logs
        // TODO: handle constant RPS
        RequestMessageQueue.Clear();

        if (_settings.MaxRequests.HasValue)
        {
            // TODO: make sure its not too many requests in MaxRequests, in that case we want to top up when going low
            TopUpRequestQueue(_settings.MaxRequests.Value);
        }
        else if (_settings.Duration.HasValue)
        {
            // NOTE: how many requests to start with? depends on duration and how many clients that will go
            TopUpRequestQueue(_topupSettings.TopupAmount);
        }
    }

    private void TopUpRequestQueue(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var method = _settings.Method switch
            {
                null => HttpMethod.Get,
                "PUT" => HttpMethod.Put,
                "POST" => HttpMethod.Post,
                _ => HttpMethod.Get
            };
            var req = new HttpRequestMessage(method, _settings.Target);
            if (_settings.Headers != null)
            {
                foreach (var header in _settings.Headers)
                {
                    req.Headers.Add(header.Key, header.Value);
                }
            }

            if (_settings.JsonContent != null)
            {
                req.Content = JsonContent.Create(_settings.JsonContent);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            RequestMessageQueue.Enqueue(req);
        }
    }

    private void UpdateReport(ref Stopwatch timer, ref int accumulatedRequests, ref int accumulatedErrors,
        ref float accumulatedLatency)
    {
        GetProgress.Requests += accumulatedRequests;
        GetProgress.ErrorRatio = (float)accumulatedErrors / GetProgress.Requests;
        GetProgress.Rps = accumulatedRequests;
        GetProgress.MeanLatency = accumulatedLatency / accumulatedRequests;
        // TODO: needs to change to handle <duration>
        GetProgress.PercentDone = _settings.MaxRequests > 0
            ? (int)(_responseData.Count / (float)_settings.MaxRequests * 100)
            : (int)(TotalTimeTimer.ElapsedMilliseconds / _settings.Duration!.Value.TotalMilliseconds * 100);

        accumulatedErrors = 0;
        accumulatedLatency = 0f;
        accumulatedRequests = 0;
        timer.Restart();
    }

    public void RunTest()
    {
        Console.Out.WriteLine("Starting clients...");
        StartClients.Set();

        _responseData.Clear();
        TotalTimeTimer.Start();
        var timer = Stopwatch.StartNew();
        var accumulatedErrors = 0;
        var accumulatedMeanLatency = 0f;
        var accumulatedRequests = 0;
        while (true)
        {
            // we're done?
            if (!Clientcts.IsCancellationRequested &&
                _settings.MaxRequests.HasValue && _settings.MaxRequests == _responseData.Count)
                Clientcts.Cancel();
            if (_settings.Duration.HasValue && TotalTimeTimer.Elapsed > _settings.Duration)
                Clientcts.Cancel();

            if (_settings.Duration.HasValue)
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
        Debug.Assert(RequestMessageQueue.IsEmpty);
        UpdateReport(ref timer, ref accumulatedRequests, ref accumulatedErrors, ref accumulatedMeanLatency);
        Debug.Assert(_responseData.Count == GetProgress.Requests);

        _averageRps = (int)(GetProgress.Requests / TotalTimeTimer.Elapsed.TotalSeconds);

        Done.Set();
        TotalTimeTimer.Stop();
    }

    private static bool IsError(DataPoint dataPoint) => (int)dataPoint.Status <= 200 && (int)dataPoint.Status >= 300;

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

        foreach (var data in results)
        {
            _responseData.Add(data);
            accumulatedErrors += IsError(data) ? 1 : 0;
        }

        accumulatedMeanLatency += (float)results.Where(x => !IsError(x))
            .Sum(x => x.ResponseTime.TotalMilliseconds);
    }
}

internal class Progress
{
    public int Requests { get; set; }
    public int PercentDone { get; set; }
    public float MeanLatency { get; set; }
    public int Rps { get; set; }
    public float ErrorRatio { get; set; }
}