using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;

namespace SharpArtillery;

/// <summary>
/// Represents a virtual user that executes a flow of requests.
/// Each VU owns its own state (captured variables, cookies, HttpClient).
/// </summary>
internal sealed class VirtualUser : IDisposable
{
    private readonly int _vuIndex;
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer;
    
    /// <summary>
    /// Captured variables from response extractions.
    /// Keys are variable names (e.g., "authToken"), values are extracted strings.
    /// </summary>
    public Dictionary<string, string> Variables { get; } = new();
    
    /// <summary>
    /// Base target URL (e.g., "http://localhost:8080").
    /// </summary>
    public string BaseTarget { get; }
    
    /// <summary>
    /// Current phase name for DataPoint tagging.
    /// </summary>
    public string? PhaseName { get; set; }
    
    /// <summary>
    /// Request timeout for this VU.
    /// </summary>
    public TimeSpan RequestTimeout { get; }

    public VirtualUser(
        int vuIndex,
        string baseTarget,
        TimeSpan? requestTimeout = null,
        string? phaseName = null,
        HttpMessageHandler? handler = null)
    {
        _vuIndex = vuIndex;
        BaseTarget = baseTarget.TrimEnd('/');
        PhaseName = phaseName;
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);

        // Each VU gets its own CookieContainer for session persistence
        _cookieContainer = new CookieContainer();

        // A handler passed in (tests use a stub) is owned by the caller and may be shared by
        // several VUs, so the VU does not dispose it; it also brings its own cookie handling.
        if (handler != null)
        {
            _httpClient = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = RequestTimeout
            };
            return;
        }

        var defaultHandler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            UseCookies = true,
            // Match the existing CustomHttpClientFactory settings
            MaxConnectionsPerServer = 100,
            AutomaticDecompression = DecompressionMethods.All
        };

        _httpClient = new HttpClient(defaultHandler)
        {
            Timeout = RequestTimeout
        };
    }
    
    /// <summary>
    /// Gets the HttpClient for this VU (with its own cookie jar).
    /// </summary>
    public HttpClient HttpClient => _httpClient;
    
    /// <summary>
    /// Gets the VU index (0-based).
    /// </summary>
    public int Index => _vuIndex;
    
    /// <summary>
    /// Sets a captured variable.
    /// </summary>
    public void SetVariable(string name, string value)
    {
        Variables[name] = value;
    }
    
    /// <summary>
    /// Gets a captured variable, or null if not set.
    /// </summary>
    public string? GetVariable(string name)
    {
        return Variables.TryGetValue(name, out var value) ? value : null;
    }
    
    /// <summary>
    /// Clears all captured variables (e.g., between flow iterations).
    /// </summary>
    public void ClearVariables()
    {
        Variables.Clear();
    }
    
    /// <summary>
    /// Clears cookies (e.g., to simulate a fresh session).
    /// Note: CookieContainer doesn't have a Clear method, so cookies accumulate within a phase.
    /// </summary>
    public static void ClearCookies()
    {
        // CookieContainer doesn't have a Clear method, so we create a new one
        // This requires recreating the HttpClient, which we avoid for performance.
        // Instead, we just let cookies accumulate within a phase.
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
