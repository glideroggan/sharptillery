using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
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

    /// <summary>
    /// Handler shared by every VU instead of their own; null (the default) gives each VU its own.
    /// </summary>
    private readonly HttpMessageHandler? _handler;

    private readonly List<VirtualUser> _virtualUsers = new();
    private readonly List<Task> _vuTasks = new();
    private CancellationTokenSource _cts = new();
    
    private long _totalRequests;
    private long _totalErrors;
    private long _claimedRequests;
    private long _cancelledAtPhaseEnd;
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
    /// HTTP requests still in flight when the phase ended. They are not in the results.
    /// </summary>
    public long CancelledAtPhaseEnd => Volatile.Read(ref _cancelledAtPhaseEnd);

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
        ConcurrentQueue<DataPoint>? resultQueue = null,
        HttpMessageHandler? handler = null)
    {
        _flowSteps = flowSteps ?? throw new ArgumentNullException(nameof(flowSteps));
        _baseTarget = baseTarget ?? throw new ArgumentNullException(nameof(baseTarget));
        _requestTimeout = requestTimeout;
        _globalTimer = globalTimer;
        _resultQueue = resultQueue ?? new ConcurrentQueue<DataPoint>();
        _handler = handler;
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
        Interlocked.Exchange(ref _claimedRequests, 0);
        Interlocked.Exchange(ref _cancelledAtPhaseEnd, 0);

        for (var i = 0; i < vuCount; i++)
        {
            var vu = new VirtualUser(
                vuIndex: i,
                baseTarget: _baseTarget,
                requestTimeout: _requestTimeout,
                phaseName: PhaseName,
                handler: _handler);
            _virtualUsers.Add(vu);
        }
    }

    /// <summary>
    /// Starts all VUs executing the flow. Returns immediately.
    /// VUs run until cancelled, until the phase's requests are used up, or until the duration ends.
    /// </summary>
    /// <param name="maxRequests">HTTP requests for the whole phase, shared by every VU; think steps do not count.</param>
    /// <param name="duration">Phase length from this call; when it ends, the step each VU is in is cancelled.</param>
    public void Start(int? maxRequests = null, TimeSpan? duration = null)
    {
        _isRunning = true;

        // Start global timer if not already running
        if (!_globalTimer.IsRunning)
            _globalTimer.Start();

        if (duration is { } phaseLength)
            _cts.CancelAfter(phaseLength);

        foreach (var vu in _virtualUsers)
        {
            var vuLocal = vu;
            var task = Task.Run(async () =>
            {
                await RunVuLoopAsync(vuLocal, maxRequests, _cts.Token);
            }, _cts.Token);
            _vuTasks.Add(task);
        }
    }

    /// <summary>
    /// Hands out one request slot of the phase. Always true without a request limit; false once
    /// every slot is taken, even while earlier requests are still in flight.
    /// </summary>
    private bool TryClaimRequest(int? maxRequests)
    {
        if (maxRequests is not { } max) return true;
        return Interlocked.Increment(ref _claimedRequests) <= max;
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
        int? maxRequests,
        CancellationToken cancellationToken)
    {
        var executor = new FlowExecutor(vu, _globalTimer);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Execute each step in the flow
                foreach (var step in _flowSteps)
                {
                    // The duration timer cancels the token; a think step it cuts short returns here.
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    // Only HTTP requests use the phase's request slots, not think steps.
                    if (!step.IsThink && !TryClaimRequest(maxRequests))
                        return;

                    DataPoint dataPoint;
                    try
                    {
                        dataPoint = await executor.ExecuteStepAsync(step, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (!step.IsThink)
                            Interlocked.Increment(ref _cancelledAtPhaseEnd);
                        return;
                    }

                    // Only count HTTP requests, not think steps
                    if (!step.IsThink)
                    {
                        _resultQueue.Enqueue(dataPoint);
                        Interlocked.Increment(ref _totalRequests);

                        if (dataPoint.IsError())
                            Interlocked.Increment(ref _totalErrors);
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
