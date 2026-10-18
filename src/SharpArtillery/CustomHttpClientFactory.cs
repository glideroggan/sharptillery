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
        MaxConnectionsPerServer = MaxUsers(settings);
        var socketHandler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = MaxConnectionsPerServer,
        };

        _client = new HttpClient(socketHandler, true);
        foreach (var (name, val) in settings.Headers)
        {
            // Content-* headers belong on each request's content (RequestTemplate); the request
            // headers here would throw "Misused header name" for them.
            if (RequestTemplate.IsContentHeader(name)) continue;
            _client.DefaultRequestHeaders.Add(name, val);
        }
    }

    /// <summary>
    /// One pooled connection per user the run can reach. A user has at most one request in flight,
    /// so more connections would sit idle, and fewer would make users queue in the pool and report
    /// the pool's wait as the server's latency.
    /// </summary>
    internal int MaxConnectionsPerServer { get; }

    private static int MaxUsers(ArtilleryConfig settings)
    {
        var users = settings.Vu;

        // Auto-tune adds clients up to its maximum; the defaults live in ToSettings.
        if (settings.ToSettings().AutoTune is { } autoTune)
            users = Math.Max(users, autoTune.MaxClients);

        var phases = settings.YamlConfig?.GetPhases();
        if (phases != null)
        {
            foreach (var phase in phases)
                users = Math.Max(users, phase.Vu);
        }

        return Math.Max(1, users);
    }

    public HttpClient CreateClient() => _client;

    public void Dispose()
    {
        _client.Dispose();
    }
}
