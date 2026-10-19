#pragma warning disable CA1848
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpArtillery.Models;

namespace SharpArtillery;

internal interface ICustomHttpClientFactory
{
    HttpClient CreateClient();
}

internal sealed class Client
{
    private readonly Manager _manager;
    private readonly ICustomHttpClientFactory _httpClientFactory;
    private readonly ILogger<Client> _logger;
    private readonly int _clientIndex;
    private ClientRateLimiter? _rateLimiter;

    public Client(Manager manager, ICustomHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, int clientIndex)
    {
        _manager = manager;
        _clientIndex = clientIndex;
        _logger = loggerFactory.CreateLogger<Client>();
        _httpClientFactory = httpClientFactory;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _manager.Started.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Initialize rate limiter if RPS limiting is enabled
            if (_manager.ConstantRps.HasValue && _manager.ConstantRps.Value > 0)
            {
                _rateLimiter = new ClientRateLimiter(
                    _manager.ConstantRps.Value,
                    _manager.ClientCount,
                    _clientIndex);
            }

            var httpClient = _httpClientFactory.CreateClient();
            var template = _manager.RequestTemplate;
            var requestTimeout = _manager.Settings.RequestTimeout ?? TimeSpan.FromSeconds(5);
            var results = _manager.ResultWriter;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Apply rate limiting if enabled (wait for our turn)
                if (_rateLimiter != null)
                {
                    await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                // The slot is claimed right before the send, so a max-requests phase sends exactly
                // that many however many clients race for the last ones.
                if (!_manager.TryClaimRequest())
                    break;

                using var req = template.Build();
                var requestResults = new DataPoint
                {
                    PhaseName = _manager.Settings.PhaseName
                };

                var startedAt = Stopwatch.GetTimestamp();
                try
                {
                    using var c = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    c.CancelAfter(requestTimeout);

                    using var res = await httpClient
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c.Token)
                        .ConfigureAwait(false);
                    requestResults.Status = res.StatusCode;
                    requestResults.HasResponse = true;
                    requestResults.ResponseContentLengthBytes = res.Content.Headers.ContentLength;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The phase ended while this request was in flight. It has no outcome, so it is
                    // counted as cancelled rather than recorded as a failed request.
                    _manager.CountCancelledAtPhaseEnd();
                    break;
                }
                catch (OperationCanceledException oce)
                {
                    // Linked CTS was canceled by the per-request timeout.
                    requestResults.Status = 0;
                    requestResults.IsTimeout = true;
                    requestResults.ErrorType = "timeout";
                    requestResults.ExceptionType = oce.GetType().FullName;
                    requestResults.Error = oce.Message;
                }
                catch (AggregateException e)
                {
                    requestResults.Status = HttpStatusCode.InternalServerError;
                    requestResults.ErrorType = "aggregate_exception";
                    requestResults.ExceptionType = e.GetType().FullName;
                    requestResults.Error = e.Message;
                }
                catch (HttpRequestException e)
                {
                    requestResults.Status = HttpStatusCode.InternalServerError;
                    requestResults.ErrorType = "http_request_exception";
                    requestResults.ExceptionType = e.GetType().FullName;
                    requestResults.Error = e.Message;
                }
                catch (Exception e)
                {
                    requestResults.Status = HttpStatusCode.InternalServerError;
                    requestResults.ErrorType = "exception";
                    requestResults.ExceptionType = e.GetType().FullName;
                    requestResults.Error = e.Message;
                }

                // One stopwatch reading gives the latency, so a request never reads two clocks.
                requestResults.ResponseTime = Stopwatch.GetElapsedTime(startedAt);
                // Use GlobalTimeTimer for report timeline (spans all phases)
                requestResults.RequestTimeLine = _manager.GlobalTimeTimer.Elapsed;

                // No token here: a result that is already measured is kept even when the phase has
                // just ended, and the manager reads until this client has stopped.
                await results.WriteAsync(requestResults, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception e)
        {
            _logger.LogError("Client error: {Message}", e.Message);
        }
        finally
        {
            _manager.ClientFinished();
        }
    }
}
