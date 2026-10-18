using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SharpArtillery.Tests.Stubs;

/// <summary>
/// A request as the stub saw it. Headers are copied when the request arrives, because
/// the blast-mode client disposes the request right after sending it.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request from a delegate, so tests
/// drive the run loops without a network or a server.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _callCount;

    public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    /// <summary>Every request that reached the handler, in arrival order.</summary>
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Requests that reached the handler, counted before the response is produced.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>A stub that answers every request at once with <paramref name="status"/>.</summary>
    public static StubHttpHandler Returning(HttpStatusCode status, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    /// <summary>A stub whose requests never answer: each one ends only when it is cancelled.</summary>
    public static StubHttpHandler Hanging() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);

        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri, headers));

        var response = await _respond(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// Blast-mode client factory over a stub handler. Like the real factory it hands every
/// client the same <see cref="HttpClient"/>.
/// </summary>
internal sealed class StubHttpClientFactory : ICustomHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;

    public StubHttpClientFactory(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: false);
    }

    public HttpClient CreateClient() => _client;

    public void Dispose() => _client.Dispose();
}
