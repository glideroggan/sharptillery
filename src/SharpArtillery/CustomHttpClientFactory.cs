﻿using System;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using SharpArtillery.Configs;

namespace SharpArtillery;

internal enum FactoryEnum
{
    Microsoft, Roger
}
internal class CustomHttpClientFactory : ICustomHttpClientFactory, IDisposable
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly FactoryEnum _factory;
    private HttpClient? _con;

    public CustomHttpClientFactory(IHttpClientFactory? httpClientFactory, FactoryEnum factory,
        ArtilleryConfig settings)
    {
        _httpClientFactory = httpClientFactory;
        _factory = factory;
        switch (_factory)
        {
            case FactoryEnum.Microsoft:
                break;
            case FactoryEnum.Roger:
                var sslOptions = new SslClientAuthenticationOptions()
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                };
                var socketHandler = new SocketsHttpHandler
                {
                    SslOptions = sslOptions,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                    // TODO: maybe should scale with the number of concurrent clients?
                    MaxConnectionsPerServer = 200,
                };
                
                _con = new HttpClient(socketHandler, true);
                foreach (var (name, val) in settings.Headers)
                {
                    _con.DefaultRequestHeaders.Add(name, val);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_factory));
        }
        
    }

    public HttpClient CreateClient()
    {
        switch (_factory)
        {
            case FactoryEnum.Microsoft:
                _con = _httpClientFactory!.CreateClient("TestTarget");
                _con.DefaultRequestHeaders.ConnectionClose = false;
                return _con;
            case FactoryEnum.Roger:
                return _con!;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Dispose()
    {
        _con?.Dispose();
    }
}