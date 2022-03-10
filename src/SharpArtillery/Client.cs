using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SharpArtillery;

internal interface ICustomHttpClientFactory
{
    HttpClient CreateClient();
}

internal class Client
{
    private readonly Manager _manager;
    private readonly CancellationToken _managerClientKillToken;
    private readonly CancellationTokenSource _privateKillTokenSource = new();
    private readonly int _id;
    private readonly ICustomHttpClientFactory _httpClientFactory;

    public Client(int id, Manager manager, ICustomHttpClientFactory httpClientFactory)
    {
        _manager = manager;
        _id = id;
        _managerClientKillToken = manager.ClientKillToken;
        _httpClientFactory = httpClientFactory;
    }

    public async Task RunAsync()
    {
        while (!_managerClientKillToken.IsCancellationRequested &&
               !_privateKillTokenSource.Token.IsCancellationRequested)
        {
            // PERF: to not be hindered by race conditions, we should send a list of requests to each client instead?
            // might not work later with rampup?
            var req = await _manager.GetRequestMessageAsync();
            if (req == null)
            {
                _manager.ClientDone(_id);
                _privateKillTokenSource.Cancel();
                continue;
            }

            // TODO: probably should add another token for this call
            // Sometimes we get stuck on the SendAsync, we should abort the send
            var requestResults = new Data
            {
                StartTime = DateTime.UtcNow
            };
            try
            {
                var c = new CancellationTokenSource();
                var httpClient = _httpClientFactory.CreateClient();
                c.CancelAfter(TimeSpan.FromSeconds(5));

                var res = await httpClient.SendAsync(req, c.Token);

                // TODO: we should read out the message as soon as possible, to let go of that stream
                requestResults.Status = res.StatusCode;
            }
            catch (AggregateException)
            {
                requestResults.Status = HttpStatusCode.InternalServerError;
            }
            catch (Exception)
            {
                requestResults.Status = HttpStatusCode.InternalServerError;
            }
            requestResults.EndTime = DateTime.UtcNow;
            requestResults.ResponseTime = requestResults.EndTime - requestResults.StartTime;
            _manager.AddResponse(requestResults);
        }
    }
}