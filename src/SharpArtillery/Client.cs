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

internal class Client
{
    private readonly Manager _manager;
    private readonly ICustomHttpClientFactory _httpClientFactory;
    private readonly ILogger<Client> _logger;

    public Client(Manager manager, ICustomHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _manager = manager;
        _logger = loggerFactory.CreateLogger<Client>();
        _httpClientFactory = httpClientFactory;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _manager.StartClients.WaitOne();
            // TODO: should set in a more global kill switch here, so that if cancellation is requested, but
            // we failed on dequeue, then we will want to try to dequeue again
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_manager.RequestMessageQueue.IsEmpty) break;
                if (!_manager.RequestMessageQueue.TryDequeue(out var req))
                    continue;

                var requestResults = new DataPoint
                {
                    StartTime = DateTime.UtcNow
                };
                try
                {
                    using var c = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var httpClient = _httpClientFactory.CreateClient();
                    c.CancelAfter(_manager.Settings.RequestTimeout ?? TimeSpan.FromSeconds(5));

                    using var res = await httpClient
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c.Token)
                        .ConfigureAwait(false);
                    requestResults.Status = res.StatusCode;
                }
                catch (AggregateException e)
                {
                    requestResults.Status = HttpStatusCode.InternalServerError;
                    requestResults.Error = e.Message;
                }
                catch (Exception e)
                {
                    requestResults.Status = HttpStatusCode.InternalServerError;
                    requestResults.Error = e.Message;
                }
                finally
                {
                    req.Dispose();
                }

                requestResults.RequestReceivedTime = DateTime.UtcNow;
                requestResults.EndTime = DateTime.UtcNow;
                requestResults.ResponseTime = requestResults.EndTime - requestResults.StartTime;
                requestResults.RequestTimeLine = _manager.TotalTimeTimer.Elapsed;
                
                _manager.ResponseMessageQueue.Enqueue(requestResults); 
            }
            // sleep before killing thread to just let everything cool
            await Task.Delay(1000, cancellationToken);
            Debug.Assert(!_manager.Settings.Duration.HasValue &&
                         _manager.RequestMessageQueue.IsEmpty);
        }
        catch (AggregateException ae)
        {
            _logger.LogError("Client error: {Message}", ae.Message);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception e)
        {
            _logger.LogError("Client error: {Message}", e.Message);
        }
    }
}