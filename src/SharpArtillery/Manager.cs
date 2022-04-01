using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private readonly List<Data> _responseData = new();
    private int _clientsDone;
    private readonly List<Data> _errors = new();
    private readonly Stopwatch _totalTimeTimer = new();
    private readonly Stopwatch _secondTimer = new();
    private int _rpsCounter;
    private float _progressLastTotalLatency;
    private readonly FlagEnum _flags;
    private readonly Settings _settings;
    private int _rps;
    private readonly SemaphoreSlim _blocker;
    private int _requestsInStock;
    private Timer _resetter;
    private float _talliedRequests;

    public Manager(Settings settings)
    {
        _settings = settings;
        if (settings.ConstantRps > 0)
        {
            _blocker = new SemaphoreSlim(settings.ConstantRps.Value, settings.ConstantRps.Value);
            _flags |= FlagEnum.ConstantRps;
            _requestsInStock = settings.ConstantRps.Value;
        }

        if (settings.Duration != null && settings.MaxRequests > 0)
            throw new ArgumentException("You can't have flag duration and requests at the same time");
        _internalCancellationTokenSource = new CancellationTokenSource();
        ClientCancellationTokenSource = new CancellationTokenSource();
    }

    private CancellationTokenSource ClientCancellationTokenSource { get; }
    public CancellationToken ClientKillToken => ClientCancellationTokenSource.Token;


    public async Task<HttpRequestMessage?> GetRequestMessageAsync()
    {
        if (!_totalTimeTimer.IsRunning)
        {
            _ = HandleResponseQueueAsync();
            _totalTimeTimer.Start();
            _secondTimer.Start();
            _resetter = new Timer
            {
                AutoReset = true,
                Interval = 100,
            };
            var requestsPerInterval = _settings.ConstantRps.HasValue ? _settings.ConstantRps.Value / 1000f * 100f : 0;
            
            _resetter.Elapsed += (_, _) =>
            {
                if (_secondTimer.Elapsed.TotalMilliseconds > 1000)
                {
                    _secondTimer.Restart();
                    _rps = _rpsCounter;
                    _rpsCounter = 0;
                }

                if (_flags.HasFlag(FlagEnum.ConstantRps))
                {
                    _talliedRequests += requestsPerInterval;
                    if (_talliedRequests >= 1)
                    {
                        _requestsInStock += (int)MathF.Floor(_talliedRequests);
                        _talliedRequests -= MathF.Floor(_talliedRequests);
                    }
                }
            };
            _resetter.Enabled = true;
        }

        if (_settings.MaxRequests != null && _rpsCounter > _settings.MaxRequests) return null;
        if (_settings.Duration.HasValue && _totalTimeTimer.Elapsed >= _settings.Duration.Value) return null;
        if (!_settings.Duration.HasValue && _responseData.Count >= _settings.MaxRequests) return null;

        if (_flags.HasFlag(FlagEnum.ConstantRps))
        {
            await _blocker.WaitAsync(ClientKillToken);
            while (_requestsInStock <= 0) await Task.Delay(1);
            Interlocked.Decrement(ref _requestsInStock);
            _blocker.Release();
        }

        Interlocked.Add(ref _rpsCounter, 1);
        var method = _settings.Method switch
        {
            null => HttpMethod.Get,
            "PUT" => HttpMethod.Put,
            _ => HttpMethod.Get
        };
        // TODO: move this to something that is already prepared, so the manager can have them prepared for the client
        var req = new HttpRequestMessage(method, _settings.Target);
        if (_settings.Headers == null) return req;
        
        foreach (var header in _settings.Headers)
        {
            req.Headers.Add(header.Key, header.Value);
        }
        return req;
    }

    public void AddResponse(Data requestsResults)
    {
        requestsResults.RequestTimeLine = _totalTimeTimer.Elapsed;
        requestsResults.Rps = _rps;

        _requestResultsQueue.Enqueue(requestsResults);
    }

    private async Task HandleResponseQueueAsync()
    {
        while (!_internalCancellationTokenSource.Token.IsCancellationRequested)
        {
            if (_clientsDone == _settings.Vu && _requestResultsQueue.IsEmpty)
            {
                // we should be done, complete the report
                _totalTimeTimer.Stop();
                _resetter.Enabled = false;

                ClientCancellationTokenSource.Cancel();
                Report();
                _internalCancellationTokenSource.Cancel();
                continue;
            }

            // dequeue responses and process them
            if (_requestResultsQueue.TryDequeue(out var results))
            {
                // TODO: don't forget to handle non-OK responses
                if (results.Status == HttpStatusCode.OK)
                {
                    _responseData.Add(results);
                }
                else
                {
                    _errors.Add(results);
                }

                // update progress
                GetProgress.Requests++;
                GetProgress.ErrorRatio = (float)_errors.Count / GetProgress.Requests;
                GetProgress.Rps = results.Rps;
                GetProgress.MeanLatency =
                    (float)(_progressLastTotalLatency + results.ResponseTime.TotalMilliseconds) /
                    GetProgress.Requests;
                // TODO: needs to change to handle <duration>
                GetProgress.PercentDone = _settings.MaxRequests > 0
                    ? (int)(_responseData.Count / (float)_settings.MaxRequests * 100)
                    : (int)(_totalTimeTimer.ElapsedMilliseconds / _settings.Duration!.Value.TotalMilliseconds * 100);
                _progressLastTotalLatency =
                    (float)(_progressLastTotalLatency + results.ResponseTime.TotalMilliseconds);
            }
            else
            {
                await Task.Delay(1);
            }
        }

        _done = true;
    }

    private void Report()
    {
        int GetPercentage(List<double> doubles, float percent)
        {
            return (int)MathF.Round(percent * doubles.Count) >= doubles.Count
                ? doubles.Count - 1
                : (int)MathF.Round(percent * doubles.Count);
        }

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
            _responseData.Count));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total errors:",
            _errors.Count));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Total time:",
            $"{_totalTimeTimer.Elapsed.TotalSeconds} s"));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Requests per second:", _rps));
        var mean = _responseData.Count > 0 ? _responseData.Average(r => r.ResponseTime.TotalMilliseconds) : 0;
        Console.WriteLine(
            string.Format(CultureInfo.InvariantCulture, "{0,-30} {1,20}", "Mean latency:", $"{mean:F} ms"));
        Console.WriteLine();

        Console.WriteLine("Percentage of the requests served within a certain time");
        var t = _responseData.Select(x => x.ResponseTime.TotalMilliseconds).ToList();
        t.Sort();
        if (t.Count == 0)
        {
            // there were no completed requests at all
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
    }

    public async Task UntilDoneAsync()
    {
        // loop until manager are done with the test
        // TODO: change this to a semaphore instead, as we just wait here anyway
        while (!_done) await Task.Delay(100);
    }

    public void ClientDone(int id)
    {
        Interlocked.Add(ref _clientsDone, 1);
    }

    public Progress GetProgress { get; private set; } = new();

    // TODO: this one should be blocked until test is done
    public List<Data> Results => _responseData;

    public void Dispose()
    {
        throw new NotImplementedException();
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