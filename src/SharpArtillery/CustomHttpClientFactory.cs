using System;
using System.Net.Http;
using System.Threading;
using SharpArtillery.Configs;

namespace SharpArtillery;

internal sealed class CustomHttpClientFactory : ICustomHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;

    public CustomHttpClientFactory(ArtilleryConfig settings)
    {
        var socketHandler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            // TODO: maybe should scale with the number of concurrent clients?
            MaxConnectionsPerServer = 200,
        };

        _client = new HttpClient(socketHandler, true);
        foreach (var (name, val) in settings.Headers)
        {
            _client.DefaultRequestHeaders.Add(name, val);
        }
    }

    public HttpClient CreateClient() => _client;

    public void Dispose()
    {
        _client.Dispose();
    }
}