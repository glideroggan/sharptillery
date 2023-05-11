using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace SharpArtillery;

/*
 * BUG:
 *  - RPS is counted towards error
 *  - timeouts are not counted as errors?
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
    private readonly CancellationTokenSource _internalCancellationTokenSource;
    private readonly ConcurrentQueue<Data> _requestResultsQueue = new();
    private bool _done;
    private volatile List<Data> _responseData = new();

    private int _clientsDone;

    // private readonly List<Data> _errors = new();
    public readonly Stopwatch TotalTimeTimer = new();
    private readonly Stopwatch _secondTimer = new();
    private int _rpsCounter;
    private float _progressLastTotalLatency;
    private readonly FlagEnum _flags;
    private readonly Settings _settings;
    private int _averageRps;
    private readonly SemaphoreSlim _blocker;
    private int _requestsInStock;
    private Timer _resetter;
    private float _talliedRequests;
    private readonly ICustomHttpClientFactory _httpClientFactory;

    private readonly List<Client> _clients = new();
    public readonly ConcurrentQueue<HttpRequestMessage> RequestMessageQueue = new();
    public readonly ConcurrentQueue<Data> ResponseMessageQueue = new();

    public Manager(Settings settings, ICustomHttpClientFactory customHttpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = customHttpClientFactory;
        if (settings.ConstantRps > 0)
        {
            _blocker = new SemaphoreSlim(settings.ConstantRps.Value, settings.ConstantRps.Value);
            _flags |= FlagEnum.ConstantRps;
            _requestsInStock = settings.ConstantRps.Value;
        }

        if (settings.Duration != null && settings.MaxRequests > 0)
            throw new ArgumentException("You can't have flag duration and requests at the same time");
        _internalCancellationTokenSource = new CancellationTokenSource();
        Clientcts = new CancellationTokenSource();
    }

    private CancellationTokenSource Clientcts { get; }
    public CancellationToken ClientKillToken => Clientcts.Token;


    // public async Task<HttpRequestMessage?> GetRequestMessageAsync()
    // {
    //     if (!_totalTimeTimer.IsRunning)
    //     {
    //         _ = HandleResponseQueueAsync();
    //         _totalTimeTimer.Start();
    //         _secondTimer.Start();
    //         _resetter = new Timer
    //         {
    //             AutoReset = true,
    //             Interval = 100,
    //         };
    //         var requestsPerInterval = _settings.ConstantRps.HasValue ? _settings.ConstantRps.Value / 1000f * 100f : 0;
    //
    //         _resetter.Elapsed += (_, _) =>
    //         {
    //             if (_secondTimer.Elapsed.TotalMilliseconds > 1000)
    //             {
    //                 _secondTimer.Restart();
    //                 _rps = _rpsCounter;
    //                 _rpsCounter = 0;
    //             }
    //
    //             if (_flags.HasFlag(FlagEnum.ConstantRps))
    //             {
    //                 _talliedRequests += requestsPerInterval;
    //                 if (_talliedRequests >= 1)
    //                 {
    //                     _requestsInStock += (int)MathF.Floor(_talliedRequests);
    //                     _talliedRequests -= MathF.Floor(_talliedRequests);
    //                 }
    //             }
    //         };
    //         _resetter.Enabled = true;
    //     }
    //
    //     if (_settings.MaxRequests != null && _rpsCounter > _settings.MaxRequests) return null;
    //     if (_settings.Duration.HasValue && _totalTimeTimer.Elapsed >= _settings.Duration.Value) return null;
    //     if (!_settings.Duration.HasValue && _responseData.Count >= _settings.MaxRequests) return null;
    //
    //     if (_flags.HasFlag(FlagEnum.ConstantRps))
    //     {
    //         await _blocker.WaitAsync(ClientKillToken);
    //         while (_requestsInStock <= 0) await Task.Delay(1);
    //         Interlocked.Decrement(ref _requestsInStock);
    //         _blocker.Release();
    //     }
    //
    //     Interlocked.Add(ref _rpsCounter, 1);
    //     var method = _settings.Method switch
    //     {
    //         null => HttpMethod.Get,
    //         "PUT" => HttpMethod.Put,
    //         "POST" => HttpMethod.Post,
    //         _ => HttpMethod.Get
    //     };
    //     // TODO: move this to something that is already prepared, so the manager can have them prepared for the client
    //     var req = new HttpRequestMessage(method, _settings.Target);
    //     if (_settings.Headers != null)
    //     {
    //         foreach (var header in _settings.Headers)
    //         {
    //             req.Headers.Add(header.Key, header.Value);
    //         }
    //     }
    //
    //     if (_settings.JsonContent != null)
    //     {
    //         req.Content = JsonContent.Create(_settings.JsonContent);
    //         req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    //     }
    //
    //     return req;
    // }

    // public void AddResponse(Data requestsResults)
    // {
    //     requestsResults.RequestTimeLine = _totalTimeTimer.Elapsed;
    //     requestsResults.Rps = _rps;
    //
    //     _requestResultsQueue.Enqueue(requestsResults);
    // }

    // private async Task HandleResponseQueueAsync()
    // {
    //     while (!_internalCancellationTokenSource.Token.IsCancellationRequested)
    //     {
    //         if (_clientsDone == _settings.Vu && _requestResultsQueue.IsEmpty)
    //         {
    //             // we should be done, complete the report
    //             _totalTimeTimer.Stop();
    //             _resetter.Enabled = false;
    //
    //             Clientcts.Cancel();
    //             Report();
    //             _internalCancellationTokenSource.Cancel();
    //             continue;
    //         }
    //
    //         // dequeue responses and process them
    //         if (_requestResultsQueue.TryDequeue(out var results))
    //         {
    //             // TODO: don't forget to handle non-OK responses
    //             if (results.Status == HttpStatusCode.OK)
    //             {
    //                 _responseData.Add(results);
    //             }
    //             else
    //             {
    //                 _errors.Add(results);
    //             }
    //
    //             // update progress
    //             GetProgress.Requests++;
    //             GetProgress.ErrorRatio = (float)_errors.Count / GetProgress.Requests;
    //             GetProgress.Rps = results.Rps;
    //             GetProgress.MeanLatency =
    //                 (float)(_progressLastTotalLatency + results.ResponseTime.TotalMilliseconds) /
    //                 GetProgress.Requests;
    //             // TODO: needs to change to handle <duration>
    //             GetProgress.PercentDone = _settings.MaxRequests > 0
    //                 ? (int)(_responseData.Count / (float)_settings.MaxRequests * 100)
    //                 : (int)(_totalTimeTimer.ElapsedMilliseconds / _settings.Duration!.Value.TotalMilliseconds * 100);
    //             _progressLastTotalLatency =
    //                 (float)(_progressLastTotalLatency + results.ResponseTime.TotalMilliseconds);
    //         }
    //         else
    //         {
    //             await Task.Delay(1);
    //         }
    //     }
    //
    //     _done = true;
    // }

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

        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Target URL:", _settings.Target));
        Console.WriteLine(_settings.MaxRequests > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Max requests:", _settings.MaxRequests)
            : string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Duration:",
                _settings.Duration!.Value.ToString()));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Concurrency level:",
            _settings.Vu));
        Console.WriteLine();

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Completed requests:",
            okRequests.Count + errorRequests.Count));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total errors:",
            errorRequests.Count));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total time:",
            $"{TotalTimeTimer.Elapsed.TotalSeconds} s"));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Requests per second:",
            _averageRps));
        var mean = _responseData.Count > 0 ? okRequests.Average(r => r.ResponseTime.TotalMilliseconds) : 0;
        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Mean latency:", $"{mean:F} ms"));
        Console.WriteLine();

        Console.WriteLine("Percentage of the OK requests served within a certain time");
        var t = okRequests.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        t.Sort();
        if (t.Count == 0)
        {
            // there were no completed requests at all
            Console.WriteLine("No requests went fine!");
        }
        else
        {
            // 50% take median
            var i = GetPercentage(t, .5f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "50%", $"{t[i]:F} ms"));
            // 90%
            i = GetPercentage(t, .9f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "90%", $"{t[i]:F} ms"));
            // 95%
            i = GetPercentage(t, .95f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "95%", $"{t[i]:F} ms"));
            // 99% divide all response-time into 100
            i = GetPercentage(t, .99f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "99%", $"{t[i]:F} ms"));
            // 100% take highest response-time, everything is faster than this
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "100%", $"{t[^1]:F} ms"));
        }

        Console.WriteLine("Percentage of the ERROR requests served within a certain time");
        var errorList = errorRequests.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        errorList.Sort();
        if (errorList.Count == 0)
        {
            // there were no completed requests at all
            Console.WriteLine("No Errors");
        }
        else
        {
            // 50% take median
            var i = GetPercentage(errorList, .5f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "50%",
                $"{errorList[i]:F} ms"));
            // 90%
            i = GetPercentage(errorList, .9f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "90%",
                $"{errorList[i]:F} ms"));
            // 95%
            i = GetPercentage(errorList, .95f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "95%",
                $"{errorList[i]:F} ms"));
            // 99% divide all response-time into 100
            i = GetPercentage(errorList, .99f);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "99%",
                $"{errorList[i]:F} ms"));
            // 100% take highest response-time, everything is faster than this
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "100%",
                $"{errorList[^1]:F} ms"));
        }
    }

    // internal class DataComparer : IComparer<Data>, IComparer
    // {
    //     public int Compare(Data a, Data b)
    //     {
    //         return a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
    //             a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1;
    //     }
    // }

    public async Task ProcessData()
    {
        Console.Write("Processing data...");

        // calculate the RPS for each request data by looking at each data point and take all other points
        // before it (up to a second) and count the number of requests. This should be the accurate number of rps
        var timer = Stopwatch.StartNew();
        _responseData.Sort((a, b) =>
            a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
            a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

        int? lastCountedNumerOfRequests = null;
        int? lastIndex = null;
        var firstOne = _responseData[0];
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

            Console.Write(".");
            timer.Restart();
        }

        Console.WriteLine();

        // loop until manager are done with the test
        // TODO: change this to a semaphore instead, as we just wait here anyway
        while (!_done) await Task.Delay(1000);
    }

    public volatile Progress GetProgress = new();

    // TODO: this one should be blocked until test is done
    public List<Data> Results => _responseData;

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask PrepareForTest()
    {
        Console.WriteLine("Preparing clients...");
        // create clients
        _clients.Clear();
        for (var i = 0; i < _settings.Vu; i++)
        {
            var c = new Client(i, this, _httpClientFactory);
            _clients.Add(c);
        }

        // create request queue
        Console.WriteLine("Preparing requests...");
        PrepareRequestQueue();

        // activate the clients (not starting test)
        Console.WriteLine("Activating clients...");
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
        for (var i = 0; i < _settings.MaxRequests; i++)
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

    internal readonly ManualResetEvent StartClients = new(false);

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
        Console.WriteLine("Starting clients...");
        StartClients.Set();

        _responseData.Clear();
        TotalTimeTimer.Start();
        var timer = Stopwatch.StartNew();
        var accumulatedErrors = 0;
        // var accumulatedTime = TimeSpan.Zero;
        var accumulatedMeanLatency = 0f;
        var accumulatedRequests = 0;
        while (true)
        {
            // we're done?
            if (!Clientcts.IsCancellationRequested && _settings.MaxRequests == _responseData.Count)
                Clientcts.Cancel();

            // TODO: top up requests (once we are not filling up the queue at once)

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

        _done = true;
        TotalTimeTimer.Stop();
    }

    private static bool IsError(Data data) => (int)data.Status <= 200 && (int)data.Status >= 300;

    private void DequeueAndAccumulate(ref int accumulatedRequests, ref int accumulatedErrors,
        ref float accumulatedMeanLatency)
    {
        var results = new List<Data>();
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