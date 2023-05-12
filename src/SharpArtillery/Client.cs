#pragma warning disable CA1848
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SharpArtillery;

internal interface ICustomHttpClientFactory
{
    HttpClient CreateClient();
}

internal class Client
{
    private readonly Manager _manager;
    private readonly CancellationToken _managerClientKillToken;
    private readonly ICustomHttpClientFactory _httpClientFactory;
    private readonly ILogger<Client> _logger;

    public Client(Manager manager, ICustomHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _manager = manager;
        _logger = loggerFactory.CreateLogger<Client>();
        _managerClientKillToken = manager.ClientKillToken;
        _httpClientFactory = httpClientFactory;
    }

    public async void Run()
    {
        try
        {
            _manager.StartClients.WaitOne();
            // TODO: should set in a more global kill switch here, so that if cancellation is requested, but
            // we failed on dequeue, then we will want to try to dequeue again
            while (!_managerClientKillToken.IsCancellationRequested)
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
                    var c = new CancellationTokenSource();
                    // TODO: what happens if we have more clients than concurrent connections/sockets in the
                    // http factory? maybe we should adjust for that
                    var httpClient = _httpClientFactory.CreateClient();
                    // TODO: make the timeout configurable
                    c.CancelAfter(TimeSpan.FromSeconds(5));

                    var res = await httpClient.SendAsync(req, c.Token);

                    // TODO: we should read out the message as soon as possible, to let go of that stream
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

                requestResults.RequestReceivedTime = DateTime.UtcNow;
                requestResults.EndTime = DateTime.UtcNow;
                requestResults.ResponseTime = requestResults.EndTime - requestResults.StartTime;
                requestResults.RequestTimeLine = _manager.TotalTimeTimer.Elapsed;
                
                _manager.ResponseMessageQueue.Enqueue(requestResults); 
            }
            // sleep before killing thread to just let everything cool
            await Task.Delay(1000);
            Debug.Assert(_manager.RequestMessageQueue.IsEmpty);
        }
        catch (AggregateException ae)
        {
            _logger.LogError("Client error: {Message}", ae.Message);
        }
        catch (Exception e)
        {
            _logger.LogError("Client error: {Message}", e.Message);
        }
    }
}