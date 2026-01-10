using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;

namespace SharpArtillery;

/// <summary>
/// Orchestrates flow-based load testing where each VirtualUser executes
/// a sequence of flow steps (HTTP requests + think times) in order.
/// This is an alternative to Manager's "blast mode" which uses a shared request queue.
/// </summary>
internal sealed class FlowRunner : IDisposable
{
    private readonly List<FlowStep> _flowSteps;
    private readonly string _baseTarget;
    private readonly TimeSpan _requestTimeout;
    private readonly Stopwatch _globalTimer;
    private readonly ConcurrentQueue<DataPoint> _resultQueue;
    
    private readonly List<VirtualUser> _virtualUsers = new();
    private readonly List<Task> _vuTasks = new();
    private CancellationTokenSource _cts = new();
    
    private long _totalRequests;
    private long _totalErrors;
    private volatile bool _isRunning;
    
    public string? PhaseName { get; set; }
    
    /// <summary>
    /// Queue for results - can be shared with Manager for unified reporting.
    /// </summary>
    public ConcurrentQueue<DataPoint> ResultQueue => _resultQueue;
    
    /// <summary>
    /// Total HTTP requests completed (excludes think steps).
    /// </summary>
    public long TotalRequests => Volatile.Read(ref _totalRequests);
    
    /// <summary>
    /// Total errors encountered.
    /// </summary>
    public long TotalErrors => Volatile.Read(ref _totalErrors);
    
    /// <summary>
    /// Whether the runner is currently executing (any VU still running).
    /// </summary>
    public bool IsRunning
    {
        get
        {
            if (!_isRunning) return false;
            
            // Check if any task is still running
            foreach (var task in _vuTasks)
            {
                if (!task.IsCompleted)
                    return true;
            }
            
            // All tasks completed, update flag
            _isRunning = false;
            return false;
        }
    }

    public FlowRunner(
        List<FlowStep> flowSteps,
        string baseTarget,
        TimeSpan requestTimeout,
        Stopwatch globalTimer,
        ConcurrentQueue<DataPoint>? resultQueue = null)
    {
        _flowSteps = flowSteps ?? throw new ArgumentNullException(nameof(flowSteps));
        _baseTarget = baseTarget ?? throw new ArgumentNullException(nameof(baseTarget));
        _requestTimeout = requestTimeout;
        _globalTimer = globalTimer;
        _resultQueue = resultQueue ?? new ConcurrentQueue<DataPoint>();
    }

    /// <summary>
    /// Prepares VirtualUsers for the test.
    /// </summary>
    public void Prepare(int vuCount)
    {
        _cts = new CancellationTokenSource();
        _virtualUsers.Clear();
        _vuTasks.Clear();
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        
        for (var i = 0; i < vuCount; i++)
        {
            var vu = new VirtualUser(
                vuIndex: i,
                baseTarget: _baseTarget,
                requestTimeout: _requestTimeout,
                phaseName: PhaseName);
            _virtualUsers.Add(vu);
        }
    }

    /// <summary>
    /// Starts all VUs executing the flow. Returns immediately.
    /// VUs will run until cancelled or until they hit MaxRequests/Duration limits.
    /// </summary>
    public void Start(int? maxRequestsPerVu = null, TimeSpan? duration = null)
    {
        _isRunning = true;
        
        // Start global timer if not already running
        if (!_globalTimer.IsRunning)
            _globalTimer.Start();
        
        foreach (var vu in _virtualUsers)
        {
            var vuLocal = vu;
            var task = Task.Run(async () =>
            {
                await RunVuLoopAsync(vuLocal, maxRequestsPerVu, duration, _cts.Token);
            }, _cts.Token);
            _vuTasks.Add(task);
        }
    }

    /// <summary>
    /// Waits for all VUs to complete.
    /// </summary>
    public async Task WaitForCompletionAsync()
    {
        try
        {
            await Task.WhenAll(_vuTasks);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <summary>
    /// Cancels all running VUs.
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
    }

    /// <summary>
    /// Main VU loop - executes flow steps repeatedly until limits or cancellation.
    /// </summary>
    private async Task RunVuLoopAsync(
        VirtualUser vu,
        int? maxRequestsPerVu,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        var executor = new FlowExecutor(vu, _globalTimer);
        var vuStartTime = DateTime.UtcNow;
        var requestCount = 0;
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Check duration limit
                if (duration.HasValue && DateTime.UtcNow - vuStartTime >= duration.Value)
                    break;
                
                // Execute each step in the flow
                foreach (var step in _flowSteps)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    // Check duration limit before each step
                    if (duration.HasValue && DateTime.UtcNow - vuStartTime >= duration.Value)
                        break;
                    
                    var dataPoint = await executor.ExecuteStepAsync(step, cancellationToken);
                    
                    // Only count HTTP requests, not think steps
                    if (!step.IsThink)
                    {
                        _resultQueue.Enqueue(dataPoint);
                        Interlocked.Increment(ref _totalRequests);
                        
                        if (dataPoint.IsError())
                            Interlocked.Increment(ref _totalErrors);
                        
                        requestCount++;
                        
                        // Check max requests limit
                        if (maxRequestsPerVu.HasValue && requestCount >= maxRequestsPerVu.Value)
                            return;
                    }
                }
                
                // Clear variables between flow iterations (each iteration is a fresh "session")
                // Note: cookies persist to maintain session stickiness
                vu.ClearVariables();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            // Log unexpected errors but don't crash the VU
            Console.Error.WriteLine($"VU[{vu.Index}] unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets current progress snapshot.
    /// </summary>
    public (long Requests, long Errors, int ActiveVUs) GetProgress()
    {
        var activeVUs = 0;
        foreach (var task in _vuTasks)
        {
            if (!task.IsCompleted)
                activeVUs++;
        }
        
        return (TotalRequests, TotalErrors, activeVUs);
    }

    public void Dispose()
    {
        _cts.Dispose();
        foreach (var vu in _virtualUsers)
        {
            vu.Dispose();
        }
        _virtualUsers.Clear();
    }
}
